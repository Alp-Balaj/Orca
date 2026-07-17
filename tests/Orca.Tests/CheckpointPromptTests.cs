using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Tests;

public class CheckpointPromptTests
{
    [Fact]
    public void Reconciler_role_text_watches_for_scratch_artifacts_as_extras()
    {
        // BF-20: the Implementer's scratch convention (.orca/scratch/) only helps if a
        // forgotten violation still gets flagged — pins that the Reconciler's role prompt
        // actually asks for it, so the guidance can't silently regress unnoticed.
        Assert.Contains(".orca/scratch/", ReconcilerPrompt.RoleText);
        Assert.Contains("scratch", ReconcilerPrompt.RoleText, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Checkpoint Checkpoint01 = new(
        "01-tag-entity",
        "Add Tag entity + EF mapping",
        CheckpointStatus.Pending,
        ["Tag entity exists", "EF mapping compiles"]);

    private static readonly Plan Plan = new(
        1, "recipe-tagging", 1, "Recipe tagging", PlanStatus.InProgress, "RecipeApp", "main",
        DateTimeOffset.Parse("2026-07-06T14:00:00Z"), DateTimeOffset.Parse("2026-07-06T14:00:00Z"),
        new PlanningInfo(PlanningStrategyKind.SingleCandidate, null, null, null),
        [Checkpoint01],
        "Plan body.");

    [Fact]
    public void Assemble_directs_scratch_work_to_the_gitignored_orca_scratch_convention()
    {
        // BF-20: the Implementer left throwaway inspection artifacts (a standalone console
        // project, a "ZZ"-prefixed scratch test) uncleaned, and Approve's `git add -A`
        // committed them — .orca/ is already fully gitignored, so pointing scratch work
        // there makes committing it structurally impossible, not merely discouraged.
        var prompt = CheckpointPrompt.Assemble(Plan, Checkpoint01, childNote: null);

        Assert.Contains(".orca/scratch/", prompt);
    }

    private static readonly GateCommands Gates = new(
        ["dotnet", "build", "RecipeApp.sln"], ["dotnet", "test", "RecipeApp.sln"]);

    private static VerificationRecord Verification(VerificationVerdict verdict) => new(
        1, "recipe-tagging", 1, "01-tag-entity", 1,
        DateTimeOffset.Parse("2026-07-16T10:00:00Z"),
        new GateResults(GateOutcome.Pass, verdict == VerificationVerdict.Pass ? GateOutcome.Pass : GateOutcome.Fail),
        verdict == VerificationVerdict.Pass ? [] : null,
        verdict,
        "");

    [Fact]
    public void Gates_section_names_both_exact_argvs_and_says_to_prerun_them()
    {
        // SUG-11 §1: the Implementer's allowlist covers both gate commands precisely so
        // it can pre-run them — but it was never told they decide its fate.
        var prompt = CheckpointPrompt.Assemble(Plan, Checkpoint01, childNote: null, gates: Gates);

        Assert.Contains("## Gates", prompt);
        Assert.Contains("build: dotnet build RecipeApp.sln", prompt);
        Assert.Contains("tests: dotnet test RecipeApp.sln", prompt);
        Assert.Contains("exiting non-zero", prompt);
        Assert.Contains("Run both before you stop", prompt);
    }

    [Fact]
    public void Scope_discipline_and_blocked_protocol_are_always_present()
    {
        // SUG-11 §2 and §3: unrequested changes count against the attempt, and a blocked
        // criterion has a protocol (stop early, say why) instead of a discarded no-op.
        var prompt = CheckpointPrompt.Assemble(Plan, Checkpoint01, childNote: null);

        Assert.Contains("Change nothing this checkpoint does not ask for", prompt);
        Assert.Contains("If a criterion\ncannot be met, stop early and state exactly why", prompt);
        Assert.Contains("shown to the human deciding this attempt", prompt);
    }

    [Fact]
    public void First_attempt_keeps_the_fresh_branch_framing()
    {
        var prompt = CheckpointPrompt.Assemble(Plan, Checkpoint01, childNote: null, attempt: 1);

        Assert.Contains("branch created for this checkpoint", prompt);
        Assert.DoesNotContain("attempt 1, and", prompt);
    }

    [Fact]
    public void Later_attempts_state_the_number_and_the_possibly_dirty_tree()
    {
        // SUG-11 §5: "a branch created for this checkpoint" is wrong from attempt 2 on —
        // the branch is reused, possibly with prior rejected changes still present.
        var prompt = CheckpointPrompt.Assemble(Plan, Checkpoint01, childNote: null, attempt: 3);

        Assert.DoesNotContain("branch created for this checkpoint", prompt);
        Assert.Contains("this is attempt 3", prompt);
        Assert.Contains("may still hold changes from a prior attempt", prompt);
    }

    [Fact]
    public void Rejection_of_a_green_attempt_reframes_the_findings_and_carries_the_reason()
    {
        // SUG-11 §4: "the findings below are why it did not stand" over all-passing
        // findings is actively misleading — the human's reason is why.
        var feedback = new AttemptFeedback
        {
            Verification = Verification(VerificationVerdict.Pass),
            Reconciliation = null,
            Rejection = new HumanRejection(1, "wrong API naming"),
        };

        var prompt = CheckpointPrompt.Assemble(Plan, Checkpoint01, childNote: null, feedback);

        Assert.Contains("passed verification but was rejected by\nthe human", prompt);
        Assert.DoesNotContain("was verified; the findings below are why it", prompt);
        Assert.Contains("### Human rejection of attempt 1", prompt);
        Assert.Contains("wrong API naming", prompt);
    }

    [Fact]
    public void Rejection_alongside_a_failed_verification_keeps_the_failure_framing()
    {
        var feedback = new AttemptFeedback
        {
            Verification = Verification(VerificationVerdict.Fail),
            Reconciliation = null,
            Rejection = new HumanRejection(1, "also, wrong API naming"),
        };

        var prompt = CheckpointPrompt.Assemble(Plan, Checkpoint01, childNote: null, feedback);

        Assert.Contains("was verified; the findings below are why it", prompt);
        Assert.Contains("### Human rejection of attempt 1", prompt);
    }

    [Fact]
    public void A_rejection_note_alone_is_feedback_worth_prompting_with()
    {
        var feedback = new AttemptFeedback
        {
            Verification = null,
            Reconciliation = null,
            Rejection = new HumanRejection(2, "took the wrong approach entirely"),
        };

        Assert.False(feedback.IsEmpty);
        var prompt = CheckpointPrompt.Assemble(Plan, Checkpoint01, childNote: null, feedback);
        Assert.Contains("### Human rejection of attempt 2", prompt);
        Assert.Contains("took the wrong approach entirely", prompt);
    }
}
