using Orca.Cli.Tui;
using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Tests;

/// <summary>The pure outcome-summary projection the run view renders before returning to
/// the shell (tui-plan build-order step 5, item 5). Exhaustive over every
/// <see cref="CheckpointSessionOutcome"/> — the switch must be total (Interrupted is
/// unreachable until step 6 but still rendered) — mirroring the CONTENT of the CLI lane's
/// CheckpointRunCommand.Render.</summary>
public sealed class RunOutcomeTextTests
{
    private static CheckpointSessionResult Result(
        CheckpointSessionOutcome outcome, Action<ResultBuilder>? tweak = null)
    {
        var builder = new ResultBuilder { Outcome = outcome };
        tweak?.Invoke(builder);
        return builder.Build();
    }

    private sealed class ResultBuilder
    {
        public CheckpointSessionOutcome Outcome { get; set; }
        public string? CommitSha { get; set; }
        public bool DiscardedChanges { get; set; }
        public bool AutoDeferred { get; set; }
        public decimal? AttemptCostUsd { get; set; }
        public CheckpointVerificationOutcome? Verification { get; set; }

        public CheckpointSessionResult Build() => new()
        {
            Outcome = Outcome,
            Preview = TuiTestData.MakePreview(),
            CommitSha = CommitSha,
            DiscardedChanges = DiscardedChanges,
            AutoDeferred = AutoDeferred,
            AttemptCostUsd = AttemptCostUsd,
            Verification = Verification,
        };
    }

    private static string Plain(IReadOnlyList<StyledLine> lines)
        => string.Join("\n", lines.Select(l => l.PlainText()));

    [Fact]
    public void Every_outcome_renders_nonempty_without_throwing()
    {
        foreach (var outcome in Enum.GetValues<CheckpointSessionOutcome>())
        {
            var lines = RunOutcomeText.Describe(Result(outcome));
            Assert.NotEmpty(lines);
            Assert.All(lines, l => Assert.NotNull(l));
        }
    }

    [Fact]
    public void Gitignore_added_says_commit_and_rerun()
    {
        var text = Plain(RunOutcomeText.Describe(Result(CheckpointSessionOutcome.GitignoreAdded)));
        Assert.Contains(".gitignore", text);
        Assert.Contains("commit that change, then re-run", text);
        Assert.Contains("Orca never commits to your branch on its own", text);
    }

    [Fact]
    public void Invocation_failed_reflects_whether_partial_changes_were_discarded()
    {
        var kept = Plain(RunOutcomeText.Describe(Result(CheckpointSessionOutcome.InvocationFailed)));
        Assert.Contains("the invocation did not succeed", kept);
        Assert.Contains("partial changes left in place", kept);

        var discarded = Plain(RunOutcomeText.Describe(
            Result(CheckpointSessionOutcome.InvocationFailed, b => b.DiscardedChanges = true)));
        Assert.Contains("partial changes discarded", discarded);
    }

    [Fact]
    public void No_changes_treats_it_as_a_failed_attempt()
    {
        var text = Plain(RunOutcomeText.Describe(Result(CheckpointSessionOutcome.NoChanges)));
        Assert.Contains("produced no changes", text);
        Assert.Contains("failed attempt", text);
    }

    [Fact]
    public void Verification_incomplete_uses_the_stop_reason_specific_hint()
    {
        var verification = MakeIncompleteVerification(InvocationStopReason.RateLimited, "verifier");
        var text = Plain(RunOutcomeText.Describe(
            Result(CheckpointSessionOutcome.VerificationIncomplete, b => b.Verification = verification)));

        Assert.Contains("rate-limited", text);
        Assert.Contains("subscription window is exhausted", text);
        Assert.Contains("the Implementer's changes are untouched", text);
    }

    [Fact]
    public void Approved_shows_the_short_sha_and_branch()
    {
        var text = Plain(RunOutcomeText.Describe(Result(
            CheckpointSessionOutcome.Approved, b => b.CommitSha = "0123456789abcdef0000")));

        Assert.Contains("committed 0123456789ab on orca/alpha/01-a", text);
        Assert.Contains("Review and merge the branch", text);
    }

    [Fact]
    public void Rejected_reflects_whether_the_reset_ran()
    {
        var reset = Plain(RunOutcomeText.Describe(
            Result(CheckpointSessionOutcome.Rejected, b => b.DiscardedChanges = true)));
        Assert.Contains("changes discarded", reset);

        var kept = Plain(RunOutcomeText.Describe(Result(CheckpointSessionOutcome.Rejected)));
        Assert.Contains("changes left in place", kept);
    }

    [Fact]
    public void Deferred_points_at_the_resume()
    {
        var text = Plain(RunOutcomeText.Describe(Result(CheckpointSessionOutcome.Deferred)));
        Assert.Contains("re-run to pick this decision back up", text);
    }

    [Fact]
    public void Interrupted_is_rendered_even_though_the_tui_cannot_reach_it_yet()
    {
        var text = Plain(RunOutcomeText.Describe(Result(CheckpointSessionOutcome.Interrupted)));
        Assert.Contains("interrupted", text);
        Assert.Contains("not re-paid", text);
    }

    [Fact]
    public void Attempt_cost_is_appended_when_present()
    {
        var text = Plain(RunOutcomeText.Describe(
            Result(CheckpointSessionOutcome.Approved, b =>
            {
                b.CommitSha = "0123456789abcdef";
                b.AttemptCostUsd = 1.14m;
            })));

        Assert.Contains("attempt cost $1.14", text);
    }

    private static CheckpointVerificationOutcome MakeIncompleteVerification(
        InvocationStopReason reason, string role) => new()
        {
            RecordPreexisted = false,
            BuildGate = null,
            TestsGate = null,
            Reconciliation = null,
            ReconciliationPath = null,
            Record = null,
            RecordPath = null,
            FailureDetail = null,
            FailureRole = role,
            FailureStopReason = reason,
            FailureResultText = null,
            CostUsd = 0m,
        };
}
