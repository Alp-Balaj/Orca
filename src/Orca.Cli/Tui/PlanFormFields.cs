namespace Orca.Cli.Tui;

/// <summary>The deliberation-depth choice the plan-generate form offers (spec §8). Single is
/// the built path; multi is deferred and refuses — the human decides, code never does.</summary>
internal enum PlanStrategyChoice
{
    Single,
    Multi,
}

/// <summary>The raw field values the plan-generate form captures, before validation — the
/// TUI twin of <c>PlanGenerateCommand.Settings</c>. Strings are verbatim widget text;
/// <see cref="PlanFormValidation.Validate"/> trims, cross-checks, and resolves them into a
/// <see cref="PlanFormResolved"/> ready to assemble a <c>PlanningRequest</c>.</summary>
internal sealed record PlanFormFields
{
    public required string PlanId { get; init; }

    /// <summary>The inline goal brief (the multi-line box). Exactly one of this and
    /// <see cref="GoalFile"/> must be non-blank.</summary>
    public required string Goal { get; init; }

    /// <summary>A path whose content is the goal brief, read verbatim (the CLI's
    /// <c>--goal-file</c>).</summary>
    public required string GoalFile { get; init; }

    public required string Title { get; init; }

    public required string BaseBranch { get; init; }

    public required PlanStrategyChoice Strategy { get; init; }
}

/// <summary>Validated, resolved inputs — one goal brief (inline or the file's content), a
/// non-blank plan id and base branch, an optional title. What the form hands
/// <c>MainScreen.GeneratePlan</c> to build the <c>PlanningRequest</c> (adding only the target
/// repo name, which the form doesn't know).</summary>
internal sealed record PlanFormResolved
{
    public required string PlanId { get; init; }
    public required string Goal { get; init; }
    public required string? Title { get; init; }
    public required string BaseBranch { get; init; }
}

/// <summary>The outcome of validating the form: an <see cref="Error"/> to show inline (the
/// form stays open), or the <see cref="Resolved"/> inputs (the form submits). Never both.</summary>
internal readonly record struct PlanFormValidationResult(string? Error, PlanFormResolved? Resolved)
{
    public bool Ok => Error is null;
}

/// <summary>Pure validation + resolution of the plan-generate form — no Terminal.Gui types,
/// so the whole request-assembly contract is unit-tested headless. Mirrors, field for field,
/// what the CLI enforces across <c>PlanGenerateCommand.Settings.Validate</c>,
/// <c>ReadGoalFile</c>, <c>RouteStrategyToHuman</c>, and
/// <c>SingleCandidatePlanningStrategy.Prepare</c>'s empty-input guards — so the TUI form and
/// the CLI accept and reject exactly the same requests.</summary>
internal static class PlanFormValidation
{
    /// <summary>The multi-candidate refusal, kept in lockstep with
    /// <c>PlanGenerateCommand.RouteStrategyToHuman</c> (which owns the CLI-lane wording, with
    /// Spectre markup). Same substance; the TUI has no <c>--strategy</c> flag to re-run, so
    /// the actionable tail says "choose single" rather than "re-run with --strategy single".
    /// Deferred until the single-candidate path and the implementer loop are proven end to
    /// end — choosing multi refuses clearly, never silently downgrades.</summary>
    public const string MultiDeferred =
        "multi-candidate planning is deferred until the single-candidate path and the implementer loop are proven end to end (plan orca-build, checkpoint 06 acceptance criteria). Choose single, or wait for checkpoint 08 to land.";

    /// <summary>Validate the raw fields. <paramref name="readGoalFile"/> returns the file's
    /// content or null when it does not exist (the injected I/O seam — the shell passes a
    /// real file read, tests pass a fake), so the goal-file path is fully covered headless.</summary>
    public static PlanFormValidationResult Validate(PlanFormFields fields, Func<string, string?> readGoalFile)
    {
        var planId = fields.PlanId.Trim();
        if (planId.Length == 0)
            return Fail("plan id is required — it names the plan's folder and file (a lowercase slug, e.g. recipe-tagging).");

        var hasGoal = !string.IsNullOrWhiteSpace(fields.Goal);
        var hasFile = !string.IsNullOrWhiteSpace(fields.GoalFile);
        if (hasGoal == hasFile)
            return Fail("exactly one of goal or goal-file is required — the Planner has one brief, inline or from a file.");

        string goal;
        if (hasFile)
        {
            var path = fields.GoalFile.Trim();
            var content = readGoalFile(path);
            if (content is null)
                return Fail($"goal file does not exist: {path}");
            goal = content;
        }
        else
        {
            goal = fields.Goal;
        }

        // Mirrors Prepare's "the goal brief must be non-empty" guard — caught here so the
        // human fixes it in the form instead of after a refused Prepare.
        if (string.IsNullOrWhiteSpace(goal))
            return Fail("the goal brief is empty — the Planner has no other input.");

        // Deliberation depth is the human's call (spec §8); multi refuses clearly rather than
        // downgrading. Checked after inputs so a well-formed multi submit gets the real reason.
        if (fields.Strategy == PlanStrategyChoice.Multi)
            return Fail(MultiDeferred);

        var baseBranch = fields.BaseBranch.Trim();
        if (baseBranch.Length == 0)
            baseBranch = "main";   // the CLI's --base-branch default

        return new PlanFormValidationResult(null, new PlanFormResolved
        {
            PlanId = planId,
            Goal = goal,
            Title = string.IsNullOrWhiteSpace(fields.Title) ? null : fields.Title.Trim(),
            BaseBranch = baseBranch,
        });
    }

    private static PlanFormValidationResult Fail(string error) => new(error, null);
}
