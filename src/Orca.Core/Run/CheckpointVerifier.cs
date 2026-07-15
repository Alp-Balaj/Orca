using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Vault;

namespace Orca.Core.Run;

/// <summary>What one verification pipeline run produced. <see cref="Record"/> is null
/// exactly when the pipeline stopped before composing the record — an invocation-level
/// failure or an exhausted §5.4 retry — in which case <see cref="FailureDetail"/> says
/// why and re-running resumes: sub-steps whose records already exist are skipped, so an
/// interrupted attempt never pays twice for the same answer.</summary>
public sealed record CheckpointVerificationOutcome
{
    /// <summary>True when the record for this attempt already existed — nothing ran.</summary>
    public required bool RecordPreexisted { get; init; }

    public required GateCommandResult? BuildGate { get; init; }
    public required GateCommandResult? TestsGate { get; init; }
    public required ReconciliationRecord? Reconciliation { get; init; }
    public required string? ReconciliationPath { get; init; }
    public required VerificationRecord? Record { get; init; }
    public required string? RecordPath { get; init; }

    public required string? FailureDetail { get; init; }

    /// <summary>Which role's invocation stopped the pipeline ("reconciler"/"verifier"),
    /// so failure hints can name the right <c>roles.*</c> config block.</summary>
    public required string? FailureRole { get; init; }

    /// <summary>Set when an invocation-level failure (auth/rate-limit/budget/timeout)
    /// stopped the pipeline — the CLI renders its actionable per-reason message from
    /// this, together with <see cref="FailureResultText"/> (the CLI's own words).</summary>
    public required InvocationStopReason? FailureStopReason { get; init; }

    public required string? FailureResultText { get; init; }

    /// <summary>Quota spent by this pipeline run (Reconciler + Verifier invocations).</summary>
    public required decimal CostUsd { get; init; }
}

/// <summary>The two-layer Verifier plus the Reconciler, orchestrated (spec §8):
/// deterministic gates first as plain process calls; on gate failure the record is
/// written and no LLM runs (owner decision 2026-07-07 — no quota on a diff the compiler
/// rejected); otherwise the Reconciler and then the semantic Verifier run as
/// vault-write-scoped invocations under the §5.4 file contract with capped Reflexion
/// retries, and Orca composes and writes the final verification record itself — the
/// model never transcribes a machine fact, and the overall verdict is derived, never
/// asserted. Every sub-step checks for an existing record first, so an interrupted
/// pipeline resumes instead of re-paying.</summary>
public sealed class CheckpointVerifier(
    OrcaConfig config,
    GitWorkspace workspace,
    IClaudeExecutor executor,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly VaultLayout _layout = new(config.VaultRoot, config.PlansDir);
    private readonly RunLogLayout _logLayout = new(workspace.RepoRoot);
    private readonly RunStateStore _stateStore = new(workspace.RepoRoot);

    public bool HasRecord(string planId, string checkpointId, int attempt) =>
        File.Exists(_layout.VerificationFile(planId, checkpointId, attempt));

    public async Task<CheckpointVerificationOutcome> RunAsync(
        Plan plan, Checkpoint checkpoint, int attempt, IRunObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        var reader = new VaultReader(_layout);
        var writer = new VaultWriter(_layout);
        var identity = new AttemptIdentity(plan.PlanId, plan.PlanVersion, checkpoint.Id, attempt);

        var baseline = new CheckpointVerificationOutcome
        {
            RecordPreexisted = false,
            BuildGate = null,
            TestsGate = null,
            Reconciliation = null,
            ReconciliationPath = null,
            Record = null,
            RecordPath = null,
            FailureDetail = null,
            FailureRole = null,
            FailureStopReason = null,
            FailureResultText = null,
            CostUsd = 0m,
        };

        var existingRecord = reader.ReadVerificationRecords(plan.PlanId, checkpoint.Id)
            .FirstOrDefault(r => r.Attempt == attempt);
        if (existingRecord is not null)
        {
            var priorReconciliation = reader.ReadReconciliationRecords(plan.PlanId, checkpoint.Id)
                .FirstOrDefault(r => r.Attempt == attempt);
            return baseline with
            {
                RecordPreexisted = true,
                Record = existingRecord,
                RecordPath = _layout.VerificationFile(plan.PlanId, checkpoint.Id, attempt),
                Reconciliation = priorReconciliation,
                ReconciliationPath = priorReconciliation is null
                    ? null
                    : _layout.ReconciliationFile(plan.PlanId, checkpoint.Id, attempt),
            };
        }

        var maxParseRetries = config.MaxParseRetries
            ?? throw new ConfigException(
                $"maxParseRetries is required to run verification (the §5.4 parse-retry ceiling) — set it in {config.RepoConfigPath} or {config.GlobalConfigPath}");

        // Layer 1: deterministic gates — machine facts from plain process calls. Both
        // always run; recording an outcome for a command that never ran would be a lie
        // in a typed field.
        var gateRunner = new GateRunner(config.GateTimeout);
        observer?.OnRunEvent(new RunObservation.GateStarted("build", config.BuildCommand));
        var build = gateRunner.Run("build", config.BuildCommand, workspace.RepoRoot,
            _logLayout.GateOutputFile(plan.PlanId, checkpoint.Id, attempt, "build"));
        observer?.OnRunEvent(new RunObservation.GateFinished(build));
        observer?.OnRunEvent(new RunObservation.GateStarted("tests", config.TestCommand));
        var tests = gateRunner.Run("tests", config.TestCommand, workspace.RepoRoot,
            _logLayout.GateOutputFile(plan.PlanId, checkpoint.Id, attempt, "tests"));
        observer?.OnRunEvent(new RunObservation.GateFinished(tests));
        baseline = baseline with { BuildGate = build, TestsGate = tests };

        if (build.Outcome == GateOutcome.Fail || tests.Outcome == GateOutcome.Fail)
        {
            var failedRecord = ComposeRecord(identity, build, tests, checklist: null);
            var path = writer.WriteVerificationRecord(failedRecord);
            observer?.OnRunEvent(new RunObservation.VaultRecordWritten("verification", path));
            return baseline with { Record = failedRecord, RecordPath = path };
        }

        // Claude Code's sandbox refuses to mkdir or write into a directory that doesn't
        // already exist under its working directory in a non-interactive session, so
        // Orca — not the Reconciler/Verifier — must guarantee `.staging` exists first.
        Directory.CreateDirectory(_layout.StagingDirectory(plan.PlanId));

        var diff = workspace.FullDiff();
        var childNotePath = _layout.ChildNoteFile(plan.PlanId, checkpoint.Id);
        var childNote = File.Exists(childNotePath) ? reader.ReadChildNote(plan, checkpoint.Id) : null;
        var planSection = CheckpointPrompt.SpecSections(checkpoint, childNote);

        // Reconciler — skipped when this attempt's record already exists (an interrupted
        // pipeline re-run must not pay for the same verdict twice).
        var reconciliation = reader.ReadReconciliationRecords(plan.PlanId, checkpoint.Id)
            .FirstOrDefault(r => r.Attempt == attempt);
        if (reconciliation is null)
        {
            var stagingPath = _layout.StagingReconciliationFile(plan.PlanId, checkpoint.Id, attempt);
            var relativeStaging = VaultRelative(stagingPath);
            var role = await RunRoleLoopAsync(
                "reconciler", RoleName.Reconciler, ReconcilerPrompt.RoleSha256,
                identity, stagingPath, maxParseRetries,
                previousError => ReconcilerPrompt.Assemble(identity, relativeStaging, planSection, diff, previousError),
                fileText => ReconciliationRecordParser.ParseStaging(fileText, stagingPath, identity),
                observer, cancellationToken);
            baseline = baseline with { CostUsd = baseline.CostUsd + role.CostUsd };
            if (role.Failure is not null)
                return Failed(baseline, role.Failure);

            reconciliation = role.Value!;
            var reconciliationPath = writer.WriteReconciliationRecord(reconciliation);
            observer?.OnRunEvent(new RunObservation.VaultRecordWritten("reconciliation", reconciliationPath));
            File.Delete(stagingPath);
            baseline = baseline with { Reconciliation = reconciliation, ReconciliationPath = reconciliationPath };
        }
        else
        {
            baseline = baseline with
            {
                Reconciliation = reconciliation,
                ReconciliationPath = _layout.ReconciliationFile(plan.PlanId, checkpoint.Id, attempt),
            };
        }

        // Layer 2: semantic verification — only reached with both gates green.
        var semanticStaging = _layout.StagingSemanticFile(plan.PlanId, checkpoint.Id, attempt);
        var relativeSemantic = VaultRelative(semanticStaging);
        var verifierRole = await RunRoleLoopAsync(
            "verifier", RoleName.Verifier, VerifierPrompt.RoleSha256,
            identity, semanticStaging, maxParseRetries,
            previousError => VerifierPrompt.Assemble(
                identity, relativeSemantic, checkpoint.AcceptanceCriteria, build, tests, diff, previousError),
            fileText => SemanticChecklistParser.ParseStaging(
                fileText, semanticStaging, identity, checkpoint.AcceptanceCriteria),
            observer, cancellationToken);
        baseline = baseline with { CostUsd = baseline.CostUsd + verifierRole.CostUsd };
        if (verifierRole.Failure is not null)
            return Failed(baseline, verifierRole.Failure);

        var record = ComposeRecord(identity, build, tests, verifierRole.Value!);
        var recordPath = writer.WriteVerificationRecord(record);
        observer?.OnRunEvent(new RunObservation.VaultRecordWritten("verification", recordPath));
        File.Delete(semanticStaging);
        return baseline with { Record = record, RecordPath = recordPath };
    }

    private sealed record RoleFailure(string Role, string Detail, InvocationStopReason? StopReason, string? ResultText);

    private sealed record RoleResult<T>(T? Value, RoleFailure? Failure, decimal CostUsd) where T : class;

    /// <summary>One §5.4 role loop: invoke, parse the staging file, and on a
    /// parse/validation failure re-prompt with the exact error up to the ceiling — the
    /// malformed file stays in place for the model to fix. Invocation-level failures
    /// (auth, rate limit, budget, timeout) end the loop immediately and are never
    /// retried automatically (the step-6 contract).</summary>
    private async Task<RoleResult<T>> RunRoleLoopAsync<T>(
        string roleKey,
        RoleName roleName,
        string roleSha256,
        AttemptIdentity identity,
        string stagingPath,
        int maxParseRetries,
        Func<string?, string> assemblePrompt,
        Func<string, T> parseStaging,
        IRunObserver? observer,
        CancellationToken cancellationToken) where T : class
    {
        var role = config.GetRole(roleName);
        var settingsJson = config.DeniedTools.Count > 0 ? PermissionSettings.ToJson(config.DeniedTools) : null;
        var cost = 0m;
        var parseFailures = 0;
        string? previousError = null;
        var invocation = NextFreeInvocation(identity, roleKey, 1);

        while (true)
        {
            var settingsPath = settingsJson is null
                ? null
                : _logLayout.RoleSettingsFile(identity.PlanId, identity.CheckpointId, identity.Attempt, roleKey, invocation);
            if (settingsPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                File.WriteAllText(settingsPath, settingsJson);
            }

            var options = new ClaudeInvocationOptions
            {
                Prompt = assemblePrompt(previousError),
                WorkingDirectory = config.VaultRoot,
                AllowedTools = role.AllowedTools,
                MaxTurns = role.MaxTurns,
                Timeout = role.Timeout,
                Model = role.Model,
                MaxBudgetUsd = role.MaxBudgetUsd,
                SettingsFilePath = settingsPath,
            };
            var logPaths = _logLayout.RoleAttemptPaths(
                identity.PlanId, identity.CheckpointId, identity.Attempt, roleKey, invocation);

            observer?.OnRunEvent(new RunObservation.RoleStarted(
                roleName, identity.Attempt, invocation, logPaths.EventLog));
            var execution = await executor.ExecuteAsync(options, logPaths,
                observer is null ? null : new RoleStreamForwarder(observer, roleName),
                cancellationToken);
            observer?.OnRunEvent(new RunObservation.RoleFinished(roleName, execution));
            cost += execution.Result?.TotalCostUsd ?? 0m;
            RecordInvocation(identity.PlanId, roleKey, roleSha256, execution);

            if (execution.StopReason != InvocationStopReason.Succeeded)
            {
                return new RoleResult<T>(null, new RoleFailure(roleKey,
                    $"the {roleKey} invocation did not succeed (stop reason: {InvocationStopReasons.ToWire(execution.StopReason)})",
                    execution.StopReason,
                    execution.Result?.ResultText), cost);
            }

            string? parseFailure = null;
            T? value = null;
            if (!File.Exists(stagingPath))
            {
                parseFailure = $"the {roleKey} wrote no file at {VaultRelative(stagingPath)} (expected {stagingPath})";
            }
            else
            {
                try
                {
                    value = parseStaging(File.ReadAllText(stagingPath));
                }
                catch (VaultException e)
                {
                    parseFailure = e.Message;
                }
            }

            if (parseFailure is null)
                return new RoleResult<T>(value, null, cost);

            parseFailures++;
            if (parseFailures > maxParseRetries)
            {
                return new RoleResult<T>(null, new RoleFailure(roleKey,
                    $"the {roleKey}'s output failed parse/validation {parseFailures} time(s), exhausting maxParseRetries={maxParseRetries}; " +
                    $"last error: {parseFailure} — the rejected file (if any) is at {stagingPath}",
                    null, null), cost);
            }

            previousError = parseFailure;
            invocation = NextFreeInvocation(identity, roleKey, invocation + 1);
            observer?.OnRunEvent(new RunObservation.RoleParseRetry(roleName, parseFailure, invocation));
        }
    }

    private VerificationRecord ComposeRecord(
        AttemptIdentity identity, GateCommandResult build, GateCommandResult tests, SemanticChecklist? checklist)
    {
        var gates = new GateResults(build.Outcome, tests.Outcome);
        var gatesPass = gates.Build == GateOutcome.Pass && gates.Tests == GateOutcome.Pass;
        var semantic = checklist?.Criteria;
        var verdict = gatesPass && semantic is not null && semantic.All(s => s.Verdict == SemanticVerdict.Pass)
            ? VerificationVerdict.Pass
            : VerificationVerdict.Fail;

        return new VerificationRecord(
            VaultSchema.Version,
            identity.PlanId,
            identity.PlanVersion,
            identity.CheckpointId,
            identity.Attempt,
            _clock.GetUtcNow(),
            gates,
            semantic,
            verdict,
            ComposeBody(build, tests, checklist));
    }

    /// <summary>The record body: relevant excerpts only (approved step-2 rule) — one line
    /// per gate with a pointer to the full run log, the failing gates' tails, and the
    /// Verifier's prose when the semantic layer ran.</summary>
    private string ComposeBody(GateCommandResult build, GateCommandResult tests, SemanticChecklist? checklist)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("## Gates\n\n");
        AppendGateLine(sb, build);
        AppendGateLine(sb, tests);

        foreach (var gate in new[] { build, tests }.Where(g => g.Outcome == GateOutcome.Fail))
        {
            sb.Append($"\n### {gate.Gate} output (last {GateRunner.TailLineCount} lines)\n\n");
            sb.Append("````text\n");
            sb.Append(gate.OutputTail.ReplaceLineEndings("\n").TrimEnd()).Append('\n');
            sb.Append("````\n");
        }

        if (checklist is not null && checklist.Body.Trim().Length > 0)
        {
            sb.Append("\n## Verifier notes\n\n");
            sb.Append(checklist.Body.ReplaceLineEndings("\n").Trim()).Append('\n');
        }

        return sb.ToString();
    }

    private void AppendGateLine(System.Text.StringBuilder sb, GateCommandResult gate)
    {
        var log = Path.GetRelativePath(workspace.RepoRoot, gate.OutputPath).Replace('\\', '/');
        var detail = gate.TimedOut
            ? $"killed after {gate.Elapsed.TotalSeconds:0.0}s — timeout"
            : $"exit {gate.ExitCode}, {gate.Elapsed.TotalSeconds:0.0}s";
        sb.Append($"- {gate.Gate}: {SchemaEnums.ToYaml(gate.Outcome)} — `{string.Join(' ', gate.Command)}` ({detail}) — log: {log}\n");
    }

    /// <summary>Merge each role invocation's facts into run state — cost, usage, stop
    /// reason, and the role prompt hash (attribution). Run state is bookkeeping, not the
    /// source of truth (that is the vault), so a hand-deleted state file is tolerated.</summary>
    private void RecordInvocation(string planId, string roleKey, string roleSha256, ClaudeExecutionResult execution)
    {
        var state = _stateStore.Read(planId);
        if (state is null)
            return;

        var hashes = state.PromptHashes.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        hashes[roleKey] = roleSha256;
        _stateStore.Write(RunStateUpdates.Apply(
            state with { PromptHashes = hashes }, execution, _clock.GetUtcNow()));
    }

    /// <summary>First invocation number ≥ <paramref name="minimum"/> whose event log does
    /// not exist yet — run logs are append-only evidence, so a crashed invocation's
    /// leftover log is skipped past, never overwritten.</summary>
    private int NextFreeInvocation(AttemptIdentity identity, string roleKey, int minimum) =>
        NextFreeSequence.Find(minimum, invocation => File.Exists(_logLayout.RoleAttemptPaths(
            identity.PlanId, identity.CheckpointId, identity.Attempt, roleKey, invocation).EventLog));

    private string VaultRelative(string path) =>
        Path.GetRelativePath(config.VaultRoot, path).Replace('\\', '/');

    private static CheckpointVerificationOutcome Failed(
        CheckpointVerificationOutcome baseline, RoleFailure failure) => baseline with
    {
        FailureDetail = failure.Detail,
        FailureRole = failure.Role,
        FailureStopReason = failure.StopReason,
        FailureResultText = failure.ResultText,
    };
}
