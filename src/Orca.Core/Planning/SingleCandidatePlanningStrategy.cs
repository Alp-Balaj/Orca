using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Run;
using Orca.Core.Vault;

namespace Orca.Core.Planning;

/// <summary>Spec §8's routine-work path: one Planner generation per attempt, as a
/// vault-write-scoped <c>claude -p</c> invocation (spec §5.4 — a file contract, not
/// forced tool use). Orchestration rules, both owner-approved 2026-07-06:
/// <list type="bullet">
/// <item>Only a parse/validation failure of the produced plan file enters the capped
/// Reflexion retry loop (<c>maxParseRetries</c>); the exact error rides the retry
/// prompt and the malformed file stays in place for the model to fix.</item>
/// <item>An invocation-level failure — auth expiry, rate limiting, budget kill,
/// timeout — ends the run loudly with a distinct recorded stop reason and is never
/// retried automatically; the human re-runs later and attempt numbering resumes from
/// planning state.</item>
/// </list></summary>
public sealed class SingleCandidatePlanningStrategy(
    OrcaConfig config,
    IClaudeExecutor executor,
    string targetRepoRoot,
    TimeProvider? clock = null) : IPlanningStrategy
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly VaultLayout _layout = new(config.VaultRoot, config.PlansDir);
    private readonly RunLogLayout _logLayout = new(targetRepoRoot);
    private readonly PlanningStateStore _stateStore = new(targetRepoRoot);

    public PlanningStrategyKind Kind => PlanningStrategyKind.SingleCandidate;

    public PlanningPreview Prepare(PlanningRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Goal))
            throw new PlanningException("the goal brief must be non-empty — the Planner has no other input");
        if (string.IsNullOrWhiteSpace(request.TargetRepoName))
            throw new PlanningException("target repo name must be non-empty");
        if (string.IsNullOrWhiteSpace(request.BaseBranch))
            throw new PlanningException("base branch must be non-empty");

        var planFilePath = _layout.PlanFile(request.PlanId);
        var role = config.GetRole(RoleName.Planner);
        var maxParseRetries = config.MaxParseRetries
            ?? throw new ConfigException(
                $"maxParseRetries is required to run the Planner (the §5.4 parse-retry ceiling) — set it in {config.RepoConfigPath} or {config.GlobalConfigPath}");

        var state = _stateStore.Read(request.PlanId);
        switch (state)
        {
            case { Status: PlanningRunStatus.Succeeded }:
                throw new PlanningException(
                    $"planning already succeeded for '{request.PlanId}' ({_stateStore.PathFor(request.PlanId)}) — review the draft at {planFilePath}; to regenerate, delete the plan file and the planning state");
            case null when File.Exists(planFilePath):
                throw new PlanningException(
                    $"plan file already exists and no planning state claims it: {planFilePath} — refusing to plan over it (was it written by hand or on another machine? use a different plan id, or delete the file)");
            case { Strategy: var s } when s != Kind:
                throw new PlanningException(
                    $"planning state for '{request.PlanId}' was created by the '{SchemaEnums.ToYaml(s)}' strategy — refusing to resume it with '{SchemaEnums.ToYaml(Kind)}'");
        }

        var attempt = NextFreeAttempt(
            request.PlanId, (state?.Attempts.Count(a => a.Role == "planner") ?? 0) + 1);

        // A resumed run whose last attempt produced an unparseable file re-enters the
        // retry conversation; any other resume (rate limit, auth, timeout) starts the
        // prompt fresh — there is nothing to correct, only quota/credentials to restore.
        var relativePlanPath = $"{config.PlansDir}/{request.PlanId}/{request.PlanId}.md";
        var timestamp = $"{_clock.GetUtcNow().ToUniversalTime():yyyy-MM-ddTHH:mm:ss}Z";
        var lastParseFailure = state?.Attempts.LastOrDefault(a => a.Role == "planner")?.ParseFailure;

        var settingsJson = config.DeniedTools.Count > 0 ? PermissionSettings.ToJson(config.DeniedTools) : null;

        // SUG-01: decide the Scout stage. An already-valid audit (a resume, or a re-plan
        // after correcting the audit by hand) is reused — never re-paid; a missing or
        // invalid one is (re)scouted when the role is configured and not opted out.
        var auditPath = _layout.AuditFile(request.PlanId);
        var auditBody = TryReadAuditBody(auditPath);
        ScoutStagePreview? scout = null;
        string? scoutSkip;
        if (request.NoScout)
            scoutSkip = "--no-scout — planning from the goal brief alone";
        else if (config.RoleSections.Scout is null)
            scoutSkip = "roles.scout is not configured — planning from the goal brief alone";
        else if (auditBody is not null)
            scoutSkip = $"audit already exists — reusing {auditPath}";
        else
        {
            scoutSkip = null;
            var scoutRole = config.GetRole(RoleName.Scout);
            var scoutAttempt = NextFreeScoutAttempt(
                request.PlanId, (state?.Attempts.Count(a => a.Role == "scout") ?? 0) + 1);
            var scoutPrompt = ScoutPrompt.AssembleInitial(request, auditPath, timestamp);
            var scoutOptions = new ClaudeInvocationOptions
            {
                Prompt = scoutPrompt,
                WorkingDirectory = targetRepoRoot,
                AllowedTools = scoutRole.AllowedTools,
                MaxTurns = scoutRole.MaxTurns,
                Timeout = scoutRole.Timeout,
                Model = scoutRole.Model,
                MaxBudgetUsd = scoutRole.MaxBudgetUsd,
                SettingsFilePath = settingsJson is null
                    ? null
                    : _logLayout.ScoutSettingsFile(request.PlanId, scoutAttempt),
            };
            scout = new ScoutStagePreview
            {
                AuditFilePath = auditPath,
                Attempt = scoutAttempt,
                Prompt = scoutPrompt,
                PromptSha256 = CheckpointPrompt.Sha256(scoutPrompt),
                Options = scoutOptions,
                Argv = ClaudeArguments.Build(scoutOptions),
                SettingsJson = settingsJson,
                LogPaths = _logLayout.ScoutAttemptPaths(request.PlanId, scoutAttempt),
            };
        }

        // With the audit in hand now (reuse path) it is embedded here; when the Scout
        // stage runs, GenerateAsync re-assembles the Planner prompt after it succeeds.
        var prompt = lastParseFailure is null
            ? PlannerPrompt.AssembleInitial(request, relativePlanPath, timestamp, auditBody)
            : PlannerPrompt.AssembleRetry(request, relativePlanPath, timestamp, lastParseFailure, auditBody);
        var options = new ClaudeInvocationOptions
        {
            Prompt = prompt,
            WorkingDirectory = config.VaultRoot,
            AllowedTools = role.AllowedTools,
            MaxTurns = role.MaxTurns,
            Timeout = role.Timeout,
            Model = role.Model,
            MaxBudgetUsd = role.MaxBudgetUsd,
            SettingsFilePath = settingsJson is null
                ? null
                : _logLayout.PlanningSettingsFile(request.PlanId, attempt),
        };

        // SUG-06 §5: same budget preflight as checkpoint prompts — refuse before billing.
        var promptBytes = System.Text.Encoding.UTF8.GetByteCount(prompt);
        if (config.MaxPromptBytes is { } ceiling && promptBytes > ceiling)
            throw new PlanningException(
                $"the assembled Planner prompt is {promptBytes} bytes, above the configured " +
                $"maxPromptBytes={ceiling} — trim the goal brief (or raise the ceiling in " +
                $"{config.RepoConfigPath}) before paying for this invocation");

        return new PlanningPreview
        {
            Request = request,
            Strategy = Kind,
            PlanFilePath = planFilePath,
            RelativePlanPath = relativePlanPath,
            Attempt = attempt,
            MaxParseRetries = maxParseRetries,
            PriorState = state,
            RequestedTimestamp = timestamp,
            Prompt = prompt,
            PromptSha256 = CheckpointPrompt.Sha256(prompt),
            Options = options,
            Argv = ClaudeArguments.Build(options),
            SettingsJson = settingsJson,
            LogPaths = _logLayout.PlanningAttemptPaths(request.PlanId, attempt),
            Scout = scout,
            ScoutSkipReason = scoutSkip,
        };
    }

    public async Task<PlanningOutcome> GenerateAsync(
        PlanningPreview preview, IRunObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        var request = preview.Request;
        var now = _clock.GetUtcNow();
        var state = (preview.PriorState ?? new PlanningState
        {
            SchemaVersion = 1,
            PlanId = request.PlanId,
            Strategy = Kind,
            Status = PlanningRunStatus.InProgress,
            Attempts = [],
            PromptFileHashes = new Dictionary<string, string> { ["planner"] = PlannerPrompt.RoleSha256 },
            AccumulatedCostUsd = 0m,
            CreatedAt = now,
            UpdatedAt = now,
        }) with
        {
            Status = PlanningRunStatus.InProgress,
            UpdatedAt = now,
        };
        _stateStore.Write(state);

        var attempt = preview.Attempt;
        var prompt = preview.Prompt;
        var options = preview.Options;
        var logPaths = preview.LogPaths;
        var parseFailuresThisRun = 0;

        // SUG-01: the Scout stage runs first when planned. Its audit body then rides
        // every Planner assembly of this run (including §5.4 retries below).
        var auditBody = TryReadAuditBody(_layout.AuditFile(request.PlanId));
        if (preview.Scout is { } scoutStage)
        {
            var (scoutState, scoutFailure, scoutedBody) =
                await RunScoutAsync(scoutStage, preview, state, observer, cancellationToken);
            state = scoutState;
            if (scoutFailure is not null)
                return scoutFailure;

            auditBody = scoutedBody;
            prompt = PlannerPrompt.AssembleInitial(
                request, preview.RelativePlanPath, preview.RequestedTimestamp, auditBody);
            options = options with { Prompt = prompt };
        }

        while (true)
        {
            if (options.SettingsFilePath is { } settingsPath)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                File.WriteAllText(settingsPath, preview.SettingsJson!);
            }

            // Planner run logs are numbered per attempt (planning.attempt-<n>), and each
            // §5.4 parse retry advances the attempt itself — so for this role attempt and
            // invocation coincide, keeping RoleStarted's invocation matched to the log
            // file name exactly as it is for the Reconciler/Verifier.
            observer?.OnRunEvent(new RunObservation.RoleStarted(
                RoleName.Planner, attempt, attempt, logPaths.EventLog));
            var startedAt = _clock.GetUtcNow();
            var execution = await executor.ExecuteAsync(options, logPaths,
                observer is null ? null : new RoleStreamForwarder(observer, RoleName.Planner),
                cancellationToken);
            var endedAt = _clock.GetUtcNow();
            observer?.OnRunEvent(new RunObservation.RoleFinished(RoleName.Planner, execution));
            var stopReason = execution.StopReason;

            string? parseFailure = null;
            Plan? plan = null;
            if (stopReason == InvocationStopReason.Succeeded)
            {
                try
                {
                    plan = ParseAndValidate(preview, request);
                }
                catch (VaultException e)
                {
                    parseFailure = e.Message;
                }
            }

            state = Record(state, new PlanningAttempt
            {
                Attempt = attempt,
                StopReason = stopReason,
                ParseFailure = parseFailure,
                SessionId = execution.SessionId,
                CostUsd = execution.Result?.TotalCostUsd ?? 0m,
                Usage = execution.Result?.Usage,
                PromptSha256 = CheckpointPrompt.Sha256(prompt),
                StartedAt = startedAt,
                EndedAt = endedAt,
            }, endedAt);

            if (stopReason != InvocationStopReason.Succeeded)
            {
                // Invocation-level failure: loud stop, no automatic retry — the human
                // restores quota/credentials/config and re-runs; numbering resumes.
                _stateStore.Write(state = state with { Status = PlanningRunStatus.Failed });
                return Outcome(state, preview, plan: null, execution,
                    execution.Result?.ResultText is { Length: > 0 } text ? text : null);
            }

            if (parseFailure is null)
            {
                _stateStore.Write(state = state with { Status = PlanningRunStatus.Succeeded });
                // The plan file becomes the canonical record the moment it survives the
                // strict parse + generated-plan rules — that is when it "lands".
                observer?.OnRunEvent(new RunObservation.VaultRecordWritten("plan", preview.PlanFilePath));
                return Outcome(state, preview, plan, execution, failureDetail: null);
            }

            parseFailuresThisRun++;
            if (parseFailuresThisRun > preview.MaxParseRetries)
            {
                _stateStore.Write(state = state with { Status = PlanningRunStatus.Failed });
                return Outcome(state, preview, plan: null, execution,
                    $"the Planner's output failed parse/validation {parseFailuresThisRun} time(s), exhausting maxParseRetries={preview.MaxParseRetries}; last error: {parseFailure}");
            }

            _stateStore.Write(state);
            attempt = NextFreeAttempt(request.PlanId, attempt + 1);
            observer?.OnRunEvent(new RunObservation.RoleParseRetry(RoleName.Planner, parseFailure, attempt));
            prompt = PlannerPrompt.AssembleRetry(
                request, preview.RelativePlanPath, preview.RequestedTimestamp, parseFailure, auditBody);
            logPaths = _logLayout.PlanningAttemptPaths(request.PlanId, attempt);
            options = options with
            {
                Prompt = prompt,
                SettingsFilePath = options.SettingsFilePath is null
                    ? null
                    : _logLayout.PlanningSettingsFile(request.PlanId, attempt),
            };
        }
    }

    /// <summary>The audit body when a valid audit note exists at <paramref name="auditPath"/>,
    /// else null — a missing file and an invalid one both mean "no usable audit" here
    /// (the invalid one gets re-scouted; Prepare/GenerateAsync never crash on it).</summary>
    private static string? TryReadAuditBody(string auditPath)
    {
        if (!File.Exists(auditPath))
            return null;
        try
        {
            return AuditNoteParser.Parse(File.ReadAllText(auditPath), auditPath).Body;
        }
        catch (VaultException)
        {
            return null;
        }
    }

    /// <summary>The Scout stage (SUG-01): invoke against the target repo, verify the repo
    /// came through byte-for-byte untouched, parse the audit under the §5.4 retry
    /// ceiling. Returns the updated state plus either a failure outcome (loud stop, never
    /// auto-retried) or the audit body for the Planner.</summary>
    private async Task<(PlanningState State, PlanningOutcome? Failure, string? AuditBody)> RunScoutAsync(
        ScoutStagePreview stage,
        PlanningPreview preview,
        PlanningState state,
        IRunObserver? observer,
        CancellationToken cancellationToken)
    {
        var request = preview.Request;
        var hashes = state.PromptFileHashes.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        hashes["scout"] = ScoutPrompt.RoleSha256;
        _stateStore.Write(state = state with { PromptFileHashes = hashes });

        var workspace = GitWorkspace.Discover(targetRepoRoot);

        var scoutAttempt = stage.Attempt;
        var scoutPrompt = stage.Prompt;
        var scoutOptions = stage.Options;
        var scoutLogPaths = stage.LogPaths;
        var parseFailuresThisRun = 0;

        while (true)
        {
            if (scoutOptions.SettingsFilePath is { } settingsPath)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                File.WriteAllText(settingsPath, stage.SettingsJson!);
            }

            // Captured immediately before the invocation so the fingerprint brackets
            // exactly what the Scout did — not Orca's own gitignored .orca/ writes.
            var treeBefore = workspace.WorkingTreeHash();

            observer?.OnRunEvent(new RunObservation.RoleStarted(
                RoleName.Scout, scoutAttempt, scoutAttempt, scoutLogPaths.EventLog));
            var startedAt = _clock.GetUtcNow();
            var execution = await executor.ExecuteAsync(scoutOptions, scoutLogPaths,
                observer is null ? null : new RoleStreamForwarder(observer, RoleName.Scout),
                cancellationToken);
            var endedAt = _clock.GetUtcNow();
            observer?.OnRunEvent(new RunObservation.RoleFinished(RoleName.Scout, execution));
            var stopReason = execution.StopReason;

            string? parseFailure = null;
            string? auditBody = null;
            if (stopReason == InvocationStopReason.Succeeded)
            {
                // The structural guard behind "no Write on the repo": the Scout's tools
                // can't be path-scoped on CLI 2.1.201, so the tree fingerprint is the
                // enforcement — any repo mutation fails the stage loudly, never rides.
                if (workspace.WorkingTreeHash() != treeBefore)
                {
                    state = Record(state, ScoutAttempt(
                        scoutAttempt, stopReason, "the Scout modified the target repository", execution,
                        scoutPrompt, startedAt, endedAt), endedAt);
                    _stateStore.Write(state = state with { Status = PlanningRunStatus.Failed });
                    return (state, Outcome(state, preview, plan: null, execution,
                        $"the Scout modified the target repository at {targetRepoRoot} — it is read-only by contract. " +
                        "Inspect `git status` there, clean up, and re-run plan-generate."), null);
                }

                if (!File.Exists(stage.AuditFilePath))
                {
                    parseFailure = $"the Scout wrote no file at {stage.AuditFilePath}";
                }
                else
                {
                    try
                    {
                        auditBody = AuditNoteParser.Parse(
                            File.ReadAllText(stage.AuditFilePath), stage.AuditFilePath).Body;
                    }
                    catch (VaultException e)
                    {
                        parseFailure = e.Message;
                    }
                }
            }

            state = Record(state, ScoutAttempt(
                scoutAttempt, stopReason, parseFailure, execution, scoutPrompt, startedAt, endedAt), endedAt);

            if (stopReason != InvocationStopReason.Succeeded)
            {
                _stateStore.Write(state = state with { Status = PlanningRunStatus.Failed });
                return (state, Outcome(state, preview, plan: null, execution,
                    execution.Result?.ResultText is { Length: > 0 } text ? text : null), null);
            }

            if (parseFailure is null)
            {
                _stateStore.Write(state);
                observer?.OnRunEvent(new RunObservation.VaultRecordWritten("audit", stage.AuditFilePath));
                return (state, null, auditBody);
            }

            parseFailuresThisRun++;
            if (parseFailuresThisRun > preview.MaxParseRetries)
            {
                _stateStore.Write(state = state with { Status = PlanningRunStatus.Failed });
                return (state, Outcome(state, preview, plan: null, execution,
                    $"the Scout's output failed parse/validation {parseFailuresThisRun} time(s), exhausting " +
                    $"maxParseRetries={preview.MaxParseRetries}; last error: {parseFailure}"), null);
            }

            _stateStore.Write(state);
            scoutAttempt = NextFreeScoutAttempt(request.PlanId, scoutAttempt + 1);
            observer?.OnRunEvent(new RunObservation.RoleParseRetry(RoleName.Scout, parseFailure, scoutAttempt));
            scoutPrompt = ScoutPrompt.AssembleRetry(
                request, stage.AuditFilePath, preview.RequestedTimestamp, parseFailure);
            scoutLogPaths = _logLayout.ScoutAttemptPaths(request.PlanId, scoutAttempt);
            scoutOptions = scoutOptions with
            {
                Prompt = scoutPrompt,
                SettingsFilePath = scoutOptions.SettingsFilePath is null
                    ? null
                    : _logLayout.ScoutSettingsFile(request.PlanId, scoutAttempt),
            };
        }
    }

    private static PlanningAttempt ScoutAttempt(
        int attempt, InvocationStopReason stopReason, string? parseFailure,
        ClaudeExecutionResult execution, string prompt, DateTimeOffset startedAt, DateTimeOffset endedAt) => new()
    {
        Role = "scout",
        Attempt = attempt,
        StopReason = stopReason,
        ParseFailure = parseFailure,
        SessionId = execution.SessionId,
        CostUsd = execution.Result?.TotalCostUsd ?? 0m,
        PromptSha256 = CheckpointPrompt.Sha256(prompt),
        StartedAt = startedAt,
        EndedAt = endedAt,
    };

    private int NextFreeScoutAttempt(string planId, int minimum)
    {
        var attempt = minimum;
        while (File.Exists(_logLayout.ScoutAttemptPaths(planId, attempt).EventLog))
            attempt++;
        return attempt;
    }

    private Plan ParseAndValidate(PlanningPreview preview, PlanningRequest request)
    {
        if (!File.Exists(preview.PlanFilePath))
            throw new VaultException(
                $"the Planner wrote no file at {preview.RelativePlanPath} (expected {preview.PlanFilePath})");

        var plan = PlanParser.Parse(File.ReadAllText(preview.PlanFilePath), preview.PlanFilePath);
        GeneratedPlanRules.Validate(plan, request, Kind, preview.PlanFilePath);

        // SUG-04, Planner contract v2: one child note per checkpoint, validated with the
        // same fail-loud discipline as the plan itself so a missing or malformed brief
        // feeds the §5.4 retry instead of surfacing as "(none)" at implementation time.
        foreach (var checkpoint in plan.Checkpoints)
        {
            var childPath = _layout.ChildNoteFile(plan.PlanId, checkpoint.Id);
            if (!File.Exists(childPath))
                throw new VaultException(
                    $"the Planner wrote no child note for checkpoint '{checkpoint.Id}' — one per checkpoint " +
                    $"is required (expected {childPath})");

            var note = ChildNoteParser.Parse(File.ReadAllText(childPath), childPath);
            if (!string.Equals(note.PlanId, plan.PlanId, StringComparison.Ordinal))
                throw new VaultSchemaException(childPath,
                    $"child note names plan '{note.PlanId}' but belongs to plan '{plan.PlanId}'");
            if (string.IsNullOrWhiteSpace(note.Body))
                throw new VaultSchemaException(childPath,
                    $"child note for checkpoint '{checkpoint.Id}' has an empty body — an empty brief is no brief");
        }

        return plan;
    }

    /// <summary>First attempt number ≥ <paramref name="minimum"/> whose event log does
    /// not exist yet. State is the source of truth for numbering, but a crashed
    /// invocation can leave a log file state never recorded — run logs are append-only
    /// evidence (<see cref="RunLogWriter"/> refuses overwrites), so skip past them
    /// instead of dying on the collision.</summary>
    private int NextFreeAttempt(string planId, int minimum)
    {
        var attempt = minimum;
        while (File.Exists(_logLayout.PlanningAttemptPaths(planId, attempt).EventLog))
            attempt++;
        return attempt;
    }

    private static PlanningState Record(PlanningState state, PlanningAttempt attempt, DateTimeOffset now) =>
        state with
        {
            Attempts = [.. state.Attempts, attempt],
            AccumulatedCostUsd = state.AccumulatedCostUsd + attempt.CostUsd,
            UpdatedAt = now,
        };

    private static PlanningOutcome Outcome(
        PlanningState state,
        PlanningPreview preview,
        Plan? plan,
        ClaudeExecutionResult execution,
        string? failureDetail) => new()
    {
        State = state,
        PlanFilePath = preview.PlanFilePath,
        Plan = plan,
        LastExecution = execution,
        FailureDetail = failureDetail,
    };
}
