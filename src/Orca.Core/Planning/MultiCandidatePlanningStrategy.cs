using System.Text;
using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Run;
using Orca.Core.Vault;

namespace Orca.Core.Planning;

/// <summary>Spec §8's deliberation path (SUG-13), unblocked now that the
/// single-candidate loop is proven end to end: N Planner invocations under genuinely
/// varied framings (diversity from framing, never from sampling one call N times), a
/// Critic that scores them into a durable §5.4 verdict, and a synthesis pass that
/// builds the final draft from the winner while absorbing what the runners-up got
/// right. The human chose this path explicitly (<c>--strategy multi</c>); the human
/// still reviews and approves the final draft like any other. All invocations run
/// under <c>roles.planner</c> — this is planner-tier work end to end.</summary>
public sealed class MultiCandidatePlanningStrategy(
    OrcaConfig config,
    IClaudeExecutor executor,
    string targetRepoRoot,
    TimeProvider? clock = null) : IPlanningStrategy
{
    /// <summary>Candidate framings, in order. More candidates than lenses cycle with an
    /// explicit variation instruction rather than repeating verbatim.</summary>
    internal static readonly IReadOnlyList<string> Lenses =
    [
        "Frame this plan around the SIMPLEST POSSIBLE implementation: the fewest moving parts that honestly satisfy the goal; prefer boring, proven shapes; cut every nice-to-have.",
        "Frame this plan around TESTABILITY: every checkpoint lands with the strongest cheap verification story; seams chosen so criteria are trivially checkable from the diff and gates.",
        "Frame this plan around EXTENSIBILITY: the seams and contracts that make the obvious next goals cheap; isolate the expensive-to-reverse choices and make them late.",
    ];

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly VaultLayout _layout = new(config.VaultRoot, config.PlansDir);
    private readonly RunLogLayout _logLayout = new(targetRepoRoot);
    private readonly PlanningStateStore _stateStore = new(targetRepoRoot);

    public PlanningStrategyKind Kind => PlanningStrategyKind.MultiCandidate;

    private int CandidateCount => config.PlanningCandidates ?? 3;

    public PlanningPreview Prepare(PlanningRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Goal))
            throw new PlanningException("the goal brief must be non-empty — the Planner has no other input");
        if (string.IsNullOrWhiteSpace(request.TargetRepoName))
            throw new PlanningException("target repo name must be non-empty");
        if (string.IsNullOrWhiteSpace(request.BaseBranch))
            throw new PlanningException("base branch must be non-empty");

        var planFilePath = _layout.PlanFile(request.PlanId);
        _ = config.GetRole(RoleName.Planner);
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

        var timestamp = $"{_clock.GetUtcNow().ToUniversalTime():yyyy-MM-ddTHH:mm:ss}Z";
        var candidatePrompt = CandidatePrompt(request, 1, timestamp);
        var role = config.GetRole(RoleName.Planner);
        var settingsJson = config.DeniedTools.Count > 0 ? PermissionSettings.ToJson(config.DeniedTools) : null;
        var options = Options(role, candidatePrompt, settingsJson, settingsPath: null);

        return new PlanningPreview
        {
            Request = request,
            Strategy = Kind,
            PlanFilePath = planFilePath,
            RelativePlanPath = $"{config.PlansDir}/{request.PlanId}/{request.PlanId}.md",
            Attempt = 1,
            MaxParseRetries = maxParseRetries,
            PriorState = state,
            RequestedTimestamp = timestamp,
            // The preview's single-invocation fields describe candidate 1 — the honest
            // sample of what N of these will look like; --dry-run renders it plus the
            // candidate count.
            Prompt = candidatePrompt,
            PromptSha256 = CheckpointPrompt.Sha256(candidatePrompt),
            Options = options,
            Argv = ClaudeArguments.Build(options),
            SettingsJson = settingsJson,
            LogPaths = CandidateLogPaths(request.PlanId, 1, 1),
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
            PromptFileHashes = new Dictionary<string, string>
            {
                ["planner"] = PlannerPrompt.RoleSha256,
                ["critic"] = CriticPromptText.RoleSha256,
            },
            AccumulatedCostUsd = 0m,
            CreatedAt = now,
            UpdatedAt = now,
        }) with
        {
            Status = PlanningRunStatus.InProgress,
            UpdatedAt = now,
        };
        _stateStore.Write(state);

        var role = config.GetRole(RoleName.Planner);
        var settingsJson = preview.SettingsJson;

        // Stage 1: N candidates, genuinely varied framings, run concurrently — each is
        // its own process with its own logs and staging directory; state is merged once
        // all have landed (the store is not concurrent-safe, the tasks don't touch it).
        var candidateTasks = Enumerable.Range(1, CandidateCount)
            .Select(i => RunFileContractStageAsync(
                stage: $"candidate-{i}",
                role, settingsJson,
                initialPrompt: CandidatePrompt(request, i, preview.RequestedTimestamp),
                retryPrompt: error => CandidatePrompt(request, i, preview.RequestedTimestamp, error),
                targetFile: CandidateFile(request.PlanId, i),
                parse: text =>
                {
                    var plan = PlanParser.Parse(text, CandidateFile(request.PlanId, i));
                    GeneratedPlanRules.Validate(plan, request, PlanningStrategyKind.SingleCandidate,
                        CandidateFile(request.PlanId, i));
                    return plan;
                },
                logPaths: n => CandidateLogPaths(request.PlanId, i, n),
                preview.MaxParseRetries, observer, cancellationToken))
            .ToList();
        await Task.WhenAll(candidateTasks);

        var endedAt = _clock.GetUtcNow();
        foreach (var stage in candidateTasks.Select(t => t.Result))
            state = Record(state, stage.Attempts, endedAt);

        if (candidateTasks.Select(t => t.Result).FirstOrDefault(r => r.Failure is not null) is { } failed)
        {
            _stateStore.Write(state = state with { Status = PlanningRunStatus.Failed });
            return Outcome(state, preview, null, failed.LastExecution!, failed.Failure);
        }

        // Stage 2: the Critic scores all candidates into a durable verdict.
        var candidateTexts = Enumerable.Range(1, CandidateCount)
            .Select(i => File.ReadAllText(CandidateFile(request.PlanId, i)))
            .ToList();
        var critiquePath = CritiqueFile(request.PlanId);
        var criticStage = await RunFileContractStageAsync(
            stage: "critic",
            role, settingsJson,
            initialPrompt: CriticPrompt(request, candidateTexts, critiquePath),
            retryPrompt: error => CriticPrompt(request, candidateTexts, critiquePath, error),
            targetFile: critiquePath,
            parse: text => CritiqueParser.Parse(text, critiquePath, request.PlanId, CandidateCount),
            logPaths: n => StageLogPaths(request.PlanId, "critic", n),
            preview.MaxParseRetries, observer, cancellationToken);
        state = Record(state, criticStage.Attempts, _clock.GetUtcNow());
        if (criticStage.Failure is not null)
        {
            _stateStore.Write(state = state with { Status = PlanningRunStatus.Failed });
            return Outcome(state, preview, null, criticStage.LastExecution!, criticStage.Failure);
        }

        var critique = (Critique)criticStage.Parsed!;

        // Stage 3: synthesis — the final draft, built from the winner, absorbing what
        // the runners-up got right, carrying the multi_candidate provenance block.
        Plan? finalPlan = null;
        var synthesisStage = await RunFileContractStageAsync(
            stage: "synthesis",
            role, settingsJson,
            initialPrompt: SynthesisPrompt(request, preview, candidateTexts, critique),
            retryPrompt: error => SynthesisPrompt(request, preview, candidateTexts, critique, error),
            targetFile: preview.PlanFilePath,
            parse: text =>
            {
                var plan = PlanParser.Parse(text, preview.PlanFilePath);
                GeneratedPlanRules.Validate(plan, request, Kind, preview.PlanFilePath);
                if (plan.Planning.Candidates != CandidateCount || plan.Planning.Selected != critique.Selected)
                    throw new VaultSchemaException(preview.PlanFilePath,
                        $"planning block must record candidates: {CandidateCount} and selected: {critique.Selected} (the critic's verdict)");
                finalPlan = plan;
                return plan;
            },
            logPaths: n => StageLogPaths(request.PlanId, "synthesis", n),
            preview.MaxParseRetries, observer, cancellationToken);
        state = Record(state, synthesisStage.Attempts, _clock.GetUtcNow());
        if (synthesisStage.Failure is not null)
        {
            _stateStore.Write(state = state with { Status = PlanningRunStatus.Failed });
            return Outcome(state, preview, null, synthesisStage.LastExecution!, synthesisStage.Failure);
        }

        _stateStore.Write(state = state with { Status = PlanningRunStatus.Succeeded });
        observer?.OnRunEvent(new RunObservation.VaultRecordWritten("plan", preview.PlanFilePath));
        return Outcome(state, preview, finalPlan, synthesisStage.LastExecution!, null);
    }

    // --- one §5.4 stage: invoke, parse, capped retries; never throws for role failures ---

    private sealed record StageResult(
        List<PlanningAttempt> Attempts, object? Parsed, string? Failure, ClaudeExecutionResult? LastExecution);

    private async Task<StageResult> RunFileContractStageAsync(
        string stage, RoleConfig role, string? settingsJson,
        string initialPrompt, Func<string, string> retryPrompt,
        string targetFile, Func<string, object> parse,
        Func<int, RunLogPaths> logPaths,
        int maxParseRetries, IRunObserver? observer, CancellationToken cancellationToken)
    {
        var attempts = new List<PlanningAttempt>();
        var prompt = initialPrompt;
        var parseFailures = 0;
        var invocation = NextFreeInvocation(logPaths, 1);
        ClaudeExecutionResult? execution = null;

        while (true)
        {
            var paths = logPaths(invocation);
            var options = Options(role, prompt, settingsJson,
                settingsJson is null ? null : paths.EventLog + ".settings.json");
            if (options.SettingsFilePath is { } settingsPath)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                File.WriteAllText(settingsPath, settingsJson!);
            }

            observer?.OnRunEvent(new RunObservation.RoleStarted(
                RoleName.Planner, invocation, invocation, paths.EventLog));
            var startedAt = _clock.GetUtcNow();
            execution = await executor.ExecuteAsync(options, paths,
                observer is null ? null : new RoleStreamForwarder(observer, RoleName.Planner),
                cancellationToken);
            var endedAt = _clock.GetUtcNow();
            observer?.OnRunEvent(new RunObservation.RoleFinished(RoleName.Planner, execution));

            string? parseFailure = null;
            object? parsed = null;
            if (execution.StopReason == InvocationStopReason.Succeeded)
            {
                if (!File.Exists(targetFile))
                {
                    parseFailure = $"the {stage} invocation wrote no file at {targetFile}";
                }
                else
                {
                    try
                    {
                        parsed = parse(File.ReadAllText(targetFile));
                    }
                    catch (VaultException e)
                    {
                        parseFailure = e.Message;
                    }
                }
            }

            attempts.Add(new PlanningAttempt
            {
                Role = stage,
                Attempt = invocation,
                StopReason = execution.StopReason,
                ParseFailure = parseFailure,
                SessionId = execution.SessionId,
                CostUsd = execution.Result?.TotalCostUsd ?? 0m,
                PromptSha256 = CheckpointPrompt.Sha256(prompt),
                StartedAt = startedAt,
                EndedAt = endedAt,
            });

            if (execution.StopReason != InvocationStopReason.Succeeded)
            {
                return new StageResult(attempts, null,
                    execution.Result?.ResultText is { Length: > 0 } text
                        ? text
                        : $"the {stage} invocation did not succeed (stop reason: {InvocationStopReasons.ToWire(execution.StopReason)})",
                    execution);
            }

            if (parseFailure is null)
                return new StageResult(attempts, parsed, null, execution);

            parseFailures++;
            if (parseFailures > maxParseRetries)
            {
                return new StageResult(attempts, null,
                    $"the {stage} output failed parse/validation {parseFailures} time(s), exhausting maxParseRetries={maxParseRetries}; last error: {parseFailure}",
                    execution);
            }

            prompt = retryPrompt(parseFailure);
            invocation = NextFreeInvocation(logPaths, invocation + 1);
        }
    }

    // --- prompts ---

    private string CandidatePrompt(PlanningRequest request, int index, string timestamp, string? previousError = null)
    {
        var relative = Path.GetRelativePath(config.VaultRoot, CandidateFile(request.PlanId, index)).Replace('\\', '/');
        var basePrompt = previousError is null
            ? PlannerPrompt.AssembleInitial(request, relative, timestamp)
            : PlannerPrompt.AssembleRetry(request, relative, timestamp, previousError);

        var lens = Lenses[(index - 1) % Lenses.Count];
        var cycled = index > Lenses.Count
            ? " Take a deliberately different angle from the obvious reading of this framing — this is a later candidate and must not repeat an earlier one."
            : "";
        return basePrompt + $"\n## Candidate framing (candidate {index} of {CandidateCount})\n\n{lens}{cycled}\n";
    }

    private string CriticPrompt(
        PlanningRequest request, IReadOnlyList<string> candidateTexts, string critiquePath, string? previousError = null)
    {
        var relative = Path.GetRelativePath(config.VaultRoot, critiquePath).Replace('\\', '/');
        var sb = new StringBuilder();
        sb.Append(CriticPromptText.RoleText.ReplaceLineEndings("\n").TrimEnd());
        sb.Append("\n\n## Request\n\n");
        sb.Append($"- plan_id: {request.PlanId}\n");
        sb.Append($"- candidates: {candidateTexts.Count}\n");
        sb.Append($"- write the verdict to this path, relative to your working directory: {relative}\n");
        sb.Append($"- if a file already exists at that path (a crashed earlier run), replace it\n");
        for (var i = 0; i < candidateTexts.Count; i++)
        {
            sb.Append($"\n## Candidate {i + 1}\n\n");
            AppendFenced(sb, candidateTexts[i]);
        }

        if (previousError is not null)
            AppendRetry(sb, relative, previousError);
        return sb.ToString();
    }

    private string SynthesisPrompt(
        PlanningRequest request, PlanningPreview preview,
        IReadOnlyList<string> candidateTexts, Critique critique, string? previousError = null)
    {
        var basePrompt = previousError is null
            ? PlannerPrompt.AssembleInitial(request, preview.RelativePlanPath, preview.RequestedTimestamp)
            : PlannerPrompt.AssembleRetry(request, preview.RelativePlanPath, preview.RequestedTimestamp, previousError);

        var sb = new StringBuilder(basePrompt);
        sb.Append("\n## Synthesis (this run is multi-candidate)\n\n");
        sb.Append($"Candidate {critique.Selected} won the critique; build the final plan FROM it, absorbing\n");
        sb.Append("what the critique says the runners-up got right. Do not restart from scratch.\n\n");
        sb.Append("The planning block must be, exactly:\n\n");
        sb.Append("```yaml\nplanning:\n  strategy: multi_candidate\n");
        sb.Append($"  candidates: {candidateTexts.Count}\n  selected: {critique.Selected}\n");
        sb.Append($"  rationale: {YamlEmit.Quote(critique.Rationale)}\n```\n");
        sb.Append($"\n### Winning candidate ({critique.Selected})\n\n");
        AppendFenced(sb, candidateTexts[critique.Selected - 1]);
        sb.Append("\n### Critique verdict\n\n");
        AppendFenced(sb, critique.Rationale + "\n\n" + critique.Body);
        return sb.ToString();
    }

    // --- paths & small helpers ---

    private string CandidateFile(string planId, int index)
    {
        var dir = Path.Combine(_layout.StagingDirectory(planId), $"candidate-{index}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, planId + ".md");
    }

    private string CritiqueFile(string planId) =>
        Path.Combine(_layout.StagingDirectory(planId), "critique.md");

    private RunLogPaths CandidateLogPaths(string planId, int candidate, int invocation) =>
        StageLogPaths(planId, $"candidate-{candidate}", invocation);

    private RunLogPaths StageLogPaths(string planId, string stage, int invocation) => new()
    {
        EventLog = Path.Combine(_logLayout.RunsRoot, planId, $"planning.{stage}.attempt-{invocation}.ndjson"),
        Stderr = Path.Combine(_logLayout.RunsRoot, planId, $"planning.{stage}.attempt-{invocation}.stderr.txt"),
    };

    private static int NextFreeInvocation(Func<int, RunLogPaths> logPaths, int minimum)
    {
        var invocation = minimum;
        while (File.Exists(logPaths(invocation).EventLog))
            invocation++;
        return invocation;
    }

    private ClaudeInvocationOptions Options(
        RoleConfig role, string prompt, string? settingsJson, string? settingsPath) => new()
    {
        Prompt = prompt,
        WorkingDirectory = config.VaultRoot,
        AllowedTools = role.AllowedTools,
        MaxTurns = role.MaxTurns,
        Timeout = role.Timeout,
        Model = role.Model,
        MaxBudgetUsd = role.MaxBudgetUsd,
        SettingsFilePath = settingsPath,
    };

    private static PlanningState Record(PlanningState state, IReadOnlyList<PlanningAttempt> attempts, DateTimeOffset now) =>
        state with
        {
            Attempts = [.. state.Attempts, .. attempts],
            AccumulatedCostUsd = state.AccumulatedCostUsd + attempts.Sum(a => a.CostUsd),
            UpdatedAt = now,
        };

    private static PlanningOutcome Outcome(
        PlanningState state, PlanningPreview preview, Plan? plan,
        ClaudeExecutionResult execution, string? failureDetail) => new()
    {
        State = state,
        PlanFilePath = preview.PlanFilePath,
        Plan = plan,
        LastExecution = execution,
        FailureDetail = failureDetail,
    };

    private static void AppendFenced(StringBuilder sb, string content)
    {
        var text = content.ReplaceLineEndings("\n").TrimEnd();
        int longest = 0, current = 0;
        foreach (var ch in text)
        {
            current = ch == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        var fence = new string('`', Math.Max(4, longest + 1));
        sb.Append(fence).Append('\n').Append(text).Append('\n').Append(fence).Append('\n');
    }

    private static void AppendRetry(StringBuilder sb, string relativePath, string previousError)
    {
        sb.Append("\n## Retry — your previous output failed validation\n\n");
        sb.Append("Orca parsed the file you wrote and rejected it. The exact error:\n\n");
        sb.Append("```\n").Append(previousError.ReplaceLineEndings("\n").TrimEnd()).Append("\n```\n\n");
        sb.Append($"The rejected file is still at {relativePath} (unless you never wrote it). Fix it in place.\n");
    }
}

/// <summary>The Critic's embedded role prompt, hash-attributed like every other role.</summary>
public static class CriticPromptText
{
    public static string RoleText { get; } = RolePromptResource.Read("Orca.Core.Prompts.critic.md");

    public static string RoleSha256 { get; } = CheckpointPrompt.Sha256(RoleText);
}
