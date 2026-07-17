using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Git;
using Orca.Core.Run;
using Orca.Core.Vault;

namespace Orca.Tests;

/// <summary>Status as a query (§3.4): the report both front ends render, plus the
/// PlanLocator enumeration it made public. No watcher — every Build call re-reads.</summary>
public sealed class StatusReportBuilderTests : IDisposable
{
    private readonly TempGitRepo _repo = new();
    private readonly string _vault = Directory.CreateTempSubdirectory("orca-status-vault-").FullName;

    public StatusReportBuilderTests()
    {
        WritePlan("alpha-plan", "Alpha work");
        WritePlan("beta-plan", "Beta work");
    }

    public void Dispose()
    {
        _repo.Dispose();
        Directory.Delete(_vault, recursive: true);
    }

    private void WritePlan(string planId, string title)
    {
        var dir = Path.Combine(_vault, "Plans", planId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, planId + ".md"), $"""
            ---
            schema_version: 1
            plan_id: {planId}
            plan_version: 1
            title: {title}
            status: approved
            target_repo: RecipeApp
            base_branch: main
            created: 2026-07-06T14:00:00Z
            updated: 2026-07-06T14:00:00Z
            planning:
              strategy: single_candidate
            checkpoints:
              - id: 01-first
                description: First checkpoint
                status: pending
                acceptance_criteria:
                  - it works
              - id: 02-second
                description: Second checkpoint
                status: pending
                acceptance_criteria:
                  - it also works
            ---
            Plan body.
            """);
    }

    private OrcaConfig Config() => new()
    {
        RepoConfigPath = Path.Combine(_repo.Root, "orca.json"),
        GlobalConfigPath = Path.Combine(_repo.Root, "global.json"),
        VaultRoot = _vault,
        VaultName = "test-vault",
        PlansDir = "Plans",
        BuildCommand = ["dotnet", "build"],
        TestCommand = ["dotnet", "test"],
        GateTimeout = null,
        MaxParseRetries = null,
        MaxCheckpointAttempts = null,
        PlanningCandidates = null,
        DeniedTools = [],
        RoleSections = new RolesSection(),
    };

    private VaultLayout Layout => new(_vault, "Plans");

    [Fact]
    public void ListPlanIds_returns_valid_plan_directories_in_ordinal_order()
    {
        // Noise a vault accumulates: a non-plan directory, and a plan-shaped directory
        // without its spine file — neither is a plan.
        Directory.CreateDirectory(Path.Combine(_vault, "Plans", "Not A Plan"));
        Directory.CreateDirectory(Path.Combine(_vault, "Plans", "spineless-plan"));

        Assert.Equal(["alpha-plan", "beta-plan"], PlanLocator.ListPlanIds(Layout));
    }

    [Fact]
    public void ListPlanIds_throws_on_a_missing_plans_directory()
    {
        var layout = new VaultLayout(_vault, "Nowhere");
        var e = Assert.Throws<VaultException>(() => PlanLocator.ListPlanIds(layout));
        Assert.Contains("does not exist", e.Message);
    }

    [Fact]
    public void Resolution_semantics_are_unchanged()
    {
        // Multiple plans without an explicit id still refuse (no silent guessing).
        var e = Assert.Throws<VaultException>(() => PlanLocator.ResolvePlanId(Layout, null));
        Assert.Contains("multiple plans", e.Message);
        Assert.Contains("alpha-plan, beta-plan", e.Message);

        // An explicit id wins; a single-plan vault resolves without one.
        Assert.Equal("beta-plan", PlanLocator.ResolvePlanId(Layout, "beta-plan"));
        Directory.Delete(Path.Combine(_vault, "Plans", "beta-plan"), recursive: true);
        Assert.Equal("alpha-plan", PlanLocator.ResolvePlanId(Layout, null));
    }

    [Fact]
    public void Build_with_a_null_plan_id_reports_every_plan()
    {
        var report = StatusReportBuilder.Build(Config(), GitWorkspace.Discover(_repo.Root), planId: null);

        Assert.Equal(["alpha-plan", "beta-plan"], report.Plans.Select(p => p.Plan.PlanId));
        Assert.Equal(_repo.Root, report.RepoRoot);
        Assert.Equal("main", report.CurrentBranch);
        Assert.True(report.WorkingTreeClean);
        Assert.Equal(Path.GetFullPath(_vault), report.VaultRoot);
        Assert.Equal("Plans", report.PlansDir);
    }

    [Fact]
    public void Build_with_an_explicit_id_narrows_to_that_plan()
    {
        var report = StatusReportBuilder.Build(Config(), GitWorkspace.Discover(_repo.Root), "beta-plan");

        var entry = Assert.Single(report.Plans);
        Assert.Equal("beta-plan", entry.Plan.PlanId);
        Assert.Null(entry.RunState);
    }

    [Fact]
    public void Build_reports_a_dirty_tree()
    {
        _repo.WriteFile("uncommitted.txt", "dirt\n");

        var report = StatusReportBuilder.Build(Config(), GitWorkspace.Discover(_repo.Root), "alpha-plan");

        Assert.False(report.WorkingTreeClean);
    }

    [Fact]
    public void Evidence_carries_the_latest_record_of_each_kind_per_checkpoint()
    {
        var writer = new VaultWriter(Layout);
        writer.WriteVerificationRecord(Verification("alpha-plan", "01-first", attempt: 1, VerificationVerdict.Fail));
        writer.WriteVerificationRecord(Verification("alpha-plan", "01-first", attempt: 2, VerificationVerdict.Pass));
        writer.WriteReconciliationRecord(new ReconciliationRecord(
            1, "alpha-plan", 1, "01-first", 1, ReconciliationVerdict.Partial, ["the rest"], [], "Body.\n"));

        var report = StatusReportBuilder.Build(Config(), GitWorkspace.Discover(_repo.Root), "alpha-plan");
        var evidence = report.Plans[0].Evidence;

        Assert.Equal(2, evidence.Count);
        Assert.Equal(2, evidence["01-first"].LatestVerification!.Attempt);
        Assert.Equal(VerificationVerdict.Pass, evidence["01-first"].LatestVerification!.Verdict);
        Assert.Equal(1, evidence["01-first"].LatestReconciliation!.Attempt);

        // A checkpoint with no attempts yet still has its entry — both records absent.
        Assert.Null(evidence["02-second"].LatestVerification);
        Assert.Null(evidence["02-second"].LatestReconciliation);
    }

    [Fact]
    public void Run_state_rides_its_plan_entry()
    {
        new RunStateStore(_repo.Root).Write(new RunState
        {
            SchemaVersion = 1,
            PlanId = "alpha-plan",
            PlanVersion = 1,
            CheckpointIndex = 0,
            CheckpointId = "01-first",
            Attempt = 2,
            Branch = "orca/alpha-plan/01-first",
            SessionId = "session-abc",
            PromptHashes = new Dictionary<string, string>(),
            AccumulatedCostUsd = 1.25m,
            LastUsage = null,
            LastStopReason = InvocationStopReason.Succeeded,
            StartedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });

        var report = StatusReportBuilder.Build(Config(), GitWorkspace.Discover(_repo.Root), planId: null);

        var alpha = report.Plans.Single(p => p.Plan.PlanId == "alpha-plan");
        Assert.Equal(2, alpha.RunState!.Attempt);
        Assert.Equal(1.25m, alpha.RunState.AccumulatedCostUsd);
        Assert.Null(report.Plans.Single(p => p.Plan.PlanId == "beta-plan").RunState);
    }

    private static VerificationRecord Verification(
        string planId, string checkpointId, int attempt, VerificationVerdict verdict) => new(
        1, planId, 1, checkpointId, attempt, DateTimeOffset.Parse("2026-07-07T10:00:00Z"),
        new GateResults(GateOutcome.Pass, GateOutcome.Pass),
        [new SemanticCriterionResult("it works", verdict == VerificationVerdict.Pass
            ? SemanticVerdict.Pass
            : SemanticVerdict.Fail, verdict == VerificationVerdict.Pass ? null : "not yet")],
        verdict,
        "## Gates\n\n- build: pass\n- tests: pass\n");

    [Fact]
    public void A_malformed_plan_becomes_an_error_entry_in_the_all_plans_view()
    {
        // BF-13: Obsidian sync or a hand edit mid-session must not crash the TUI's
        // refresh — the malformed plan surfaces as an entry carrying the parser's reason.
        var dir = Path.Combine(_vault, "Plans", "broken-plan");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "broken-plan.md"), "not a plan at all");

        var report = StatusReportBuilder.Build(Config(), GitWorkspace.Discover(_repo.Root), planId: null);

        var broken = Assert.Single(report.Plans, p => p.Plan.PlanId == "broken-plan");
        Assert.NotNull(broken.LoadError);
        Assert.Empty(broken.Plan.Checkpoints);
        // The healthy plans are untouched.
        Assert.Contains(report.Plans, p => p.Plan.PlanId == "alpha-plan" && p.LoadError is null);
    }

    [Fact]
    public void Single_plan_resolution_stays_fail_loud_on_a_malformed_plan()
    {
        var dir = Path.Combine(_vault, "Plans", "broken-plan");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "broken-plan.md"), "not a plan at all");

        Assert.ThrowsAny<VaultException>(() =>
            StatusReportBuilder.Build(Config(), GitWorkspace.Discover(_repo.Root), "broken-plan"));
    }
}
