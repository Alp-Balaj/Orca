using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Run;
using Orca.Core.Vault;

namespace Orca.Tests;

/// <summary>SUG-10's deterministic checks against a real temp repo + vault, with the
/// external-binary probe faked — no process spawns beyond git, no quota.</summary>
public sealed class DoctorTests : IDisposable
{
    private readonly TempGitRepo _repo = new();
    private readonly string _vault = Directory.CreateTempSubdirectory("orca-doctor-vault-").FullName;

    public void Dispose()
    {
        _repo.Dispose();
        Directory.Delete(_vault, recursive: true);
    }

    private const string PlanText = """
        ---
        schema_version: 1
        plan_id: recipe-tagging
        plan_version: 1
        title: Add tagging
        status: approved
        target_repo: RecipeApp
        base_branch: main
        created: 2026-07-06T14:00:00Z
        updated: 2026-07-06T14:00:00Z
        planning:
          strategy: single_candidate
        checkpoints:
          - id: 01-tag-entity
            description: Add Tag entity
            status: pending
            acceptance_criteria: [entity exists]
        ---
        Body.
        """;

    private OrcaConfig Config(string plansDir = "Plans", string[]? build = null) => new()
    {
        RepoConfigPath = Path.Combine(_repo.Root, "orca.json"),
        GlobalConfigPath = Path.Combine(_repo.Root, "global.json"),
        VaultRoot = _vault,
        VaultName = "test-vault",
        PlansDir = plansDir,
        BuildCommand = build ?? ["dotnet", "build"],
        TestCommand = ["dotnet", "test"],
        GateTimeout = null,
        MaxParseRetries = 1,
        MaxCheckpointAttempts = 3,
        PlanningCandidates = null,
        DeniedTools = [],
        RoleSections = new RolesSection(),
    };

    private static string? HappyProbe(string binary, string[] args) => binary switch
    {
        "claude" => "2.1.201 (Claude Code)\n",
        "git" => "git version 2.54.0\n",
        _ => null,
    };

    private Doctor MakeDoctor(OrcaConfig? config = null, Func<string, string[], string?>? probe = null) =>
        new(config ?? Config(), GitWorkspace.Discover(_repo.Root), probe ?? HappyProbe);

    private void WritePlan(string text)
    {
        var dir = Path.Combine(_vault, "Plans", "recipe-tagging");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "recipe-tagging.md"), text);
    }

    [Fact]
    public void A_matching_claude_version_passes_and_a_drifted_one_warns()
    {
        WritePlan(PlanText);
        var pass = MakeDoctor().Run();
        Assert.Contains(pass, f => f.Level == DoctorLevel.Pass && f.Message.Contains("matches the pinned"));

        var drifted = MakeDoctor(probe: (b, a) => b == "claude" ? "2.1.211 (Claude Code)\n" : HappyProbe(b, a)).Run();
        var warning = Assert.Single(drifted, f => f.Message.Contains("2.1.211"));
        Assert.Equal(DoctorLevel.Warn, warning.Level);
        Assert.Contains("contract test", warning.Action);
    }

    [Fact]
    public void A_missing_claude_binary_fails()
    {
        WritePlan(PlanText);
        var findings = MakeDoctor(probe: (b, a) => b == "claude" ? null : HappyProbe(b, a)).Run();

        Assert.Contains(findings, f => f.Level == DoctorLevel.Fail && f.Message.Contains("claude is not on PATH"));
    }

    [Fact]
    public void Placeholder_gate_commands_fail_with_the_config_path_in_the_action()
    {
        WritePlan(PlanText);
        var findings = MakeDoctor(Config(build: ["REPLACE_ME"])).Run();

        var failure = Assert.Single(findings, f => f.Message.Contains("commands.build is not configured"));
        Assert.Equal(DoctorLevel.Fail, failure.Level);
    }

    [Fact]
    public void Auth_state_is_reported_unknowable_by_design_never_probed()
    {
        WritePlan(PlanText);
        var findings = MakeDoctor().Run();

        Assert.Contains(findings, f => f.Level == DoctorLevel.Info && f.Message.Contains("auth state unknown"));
    }

    [Fact]
    public void Backslashes_in_plans_dir_fail()
    {
        var findings = MakeDoctor(Config(plansDir: @"Orca\Plans")).Run();

        Assert.Contains(findings, f => f.Level == DoctorLevel.Fail && f.Message.Contains("backslashes"));
    }

    [Fact]
    public void Sync_conflict_artifacts_in_the_plans_tree_fail()
    {
        WritePlan(PlanText);
        var dir = Path.Combine(_vault, "Plans", "recipe-tagging");
        File.WriteAllText(Path.Combine(dir, "recipe-tagging.sync-conflict-20260716.md"), "x");
        File.WriteAllText(Path.Combine(dir, "recipe-tagging (1).md"), "x");

        var findings = MakeDoctor().Run();

        Assert.Equal(2, findings.Count(f => f.Level == DoctorLevel.Fail && f.Message.Contains("sync artifact")));
    }

    [Fact]
    public void An_unparseable_plan_fails_with_the_parser_error()
    {
        WritePlan("not a plan");
        var findings = MakeDoctor().Run();

        Assert.Contains(findings, f => f.Level == DoctorLevel.Fail && f.Message.Contains("does not parse"));
    }

    [Fact]
    public void A_complete_plan_with_pending_checkpoints_warns()
    {
        WritePlan(PlanText.Replace("status: approved", "status: complete"));
        var findings = MakeDoctor().Run();

        Assert.Contains(findings, f =>
            f.Level == DoctorLevel.Warn && f.Message.Contains("says complete but has non-complete"));
    }

    [Fact]
    public void Vault_records_without_a_local_branch_warn_about_the_other_machine()
    {
        WritePlan(PlanText);
        new VaultWriter(new VaultLayout(_vault, "Plans")).WriteVerificationRecord(new VerificationRecord(
            1, "recipe-tagging", 1, "01-tag-entity", 1,
            new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero),
            new GateResults(GateOutcome.Pass, GateOutcome.Pass),
            [new SemanticCriterionResult("entity exists", SemanticVerdict.Pass, null)],
            VerificationVerdict.Pass, "Body.\n"));

        var findings = MakeDoctor().Run();

        Assert.Contains(findings, f =>
            f.Level == DoctorLevel.Warn && f.Message.Contains("no local branch"));
    }

    [Fact]
    public void Orca_ignore_status_is_asked_of_git_itself()
    {
        WritePlan(PlanText);
        var before = MakeDoctor().Run();
        Assert.Contains(before, f => f.Level == DoctorLevel.Warn && f.Message.Contains(".orca/ is not git-ignored"));

        File.WriteAllText(Path.Combine(_repo.Root, ".gitignore"), ".orca/\n");
        var after = MakeDoctor().Run();
        Assert.Contains(after, f => f.Level == DoctorLevel.Pass && f.Message.Contains(".orca/ is git-ignored"));
    }

    [Fact]
    public void Target_repo_mismatch_warns()
    {
        // The temp repo's folder name is a random slug, never 'RecipeApp'.
        WritePlan(PlanText);
        var findings = MakeDoctor().Run();

        Assert.Contains(findings, f =>
            f.Level == DoctorLevel.Warn && f.Message.Contains("targets repo 'RecipeApp'"));
    }
}
