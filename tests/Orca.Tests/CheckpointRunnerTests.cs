using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Run;

namespace Orca.Tests;

public sealed class CheckpointRunnerTests : IDisposable
{
    private const string PlanText = """
        ---
        schema_version: 1
        plan_id: recipe-tagging
        plan_version: 1
        title: Add tagging to recipes
        status: approved
        target_repo: RecipeApp
        base_branch: main
        created: 2026-07-06T14:00:00Z
        updated: 2026-07-06T14:00:00Z
        planning:
          strategy: single_candidate
        checkpoints:
          - id: 01-tag-entity
            description: Add Tag entity + EF mapping
            status: pending
            acceptance_criteria:
              - Tag entity exists
              - EF mapping compiles
          - id: 02-endpoints
            description: CRUD endpoints for tags
            status: pending
            acceptance_criteria:
              - endpoints respond
        ---
        Plan body.
        """;

    private const string ChildNoteText = """
        ---
        schema_version: 1
        plan_id: recipe-tagging
        checkpoint_id: 01-tag-entity
        ---
        Create the Tag entity exactly as the recipe pattern does.
        """;

    private readonly TempGitRepo _repo = new();
    private readonly string _vault =
        Path.Combine(Path.GetTempPath(), "orca-vault-tests-" + Guid.NewGuid().ToString("N"));

    public CheckpointRunnerTests()
    {
        var planDir = Path.Combine(_vault, "Plans", "recipe-tagging");
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "recipe-tagging.md"), PlanText);
        File.WriteAllText(Path.Combine(planDir, "01-tag-entity.md"), ChildNoteText);

        // The target repo starts with .orca/ ignored and committed, as checkpoint-run
        // enforces before any real run.
        _repo.WriteFile(".gitignore", ".orca/\n");
        _repo.Git("add", "-A");
        _repo.Git("commit", "-m", "ignore .orca");
    }

    public void Dispose()
    {
        _repo.Dispose();
        try
        {
            Directory.Delete(_vault, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private OrcaConfig Config(
        string[]? deniedTools = null,
        int? maxParseRetries = 1,
        int? maxCheckpointAttempts = 3,
        bool verificationRoles = true) => new()
    {
        RepoConfigPath = Path.Combine(_repo.Root, "orca.json"),
        GlobalConfigPath = Path.Combine(_repo.Root, "global.json"),
        VaultRoot = _vault,
        VaultName = "test-vault",
        PlansDir = "Plans",
        BuildCommand = ["dotnet", "build"],
        TestCommand = ["dotnet", "test"],
        GateTimeout = null,
        MaxParseRetries = maxParseRetries,
        MaxCheckpointAttempts = maxCheckpointAttempts,
        PlanningCandidates = null,
        DeniedTools = deniedTools ?? ["Bash(git push*)"],
        RoleSections = new RolesSection
        {
            Implementer = new RoleSection
            {
                AllowedTools = ["Read", "Edit", "Bash(dotnet build*)"],
                MaxTurns = 10,
                TimeoutSeconds = 300,
            },
            Reconciler = verificationRoles
                ? new RoleSection { AllowedTools = ["Read", "Write"], MaxTurns = 10, TimeoutSeconds = 300 }
                : null,
            Verifier = verificationRoles
                ? new RoleSection { AllowedTools = ["Read", "Write"], MaxTurns = 10, TimeoutSeconds = 300 }
                : null,
        },
    };

    private CheckpointRunner Runner(
        FakeExecutor executor,
        string[]? deniedTools = null,
        int? maxParseRetries = 1,
        int? maxCheckpointAttempts = 3,
        bool verificationRoles = true) =>
        new(Config(deniedTools, maxParseRetries, maxCheckpointAttempts, verificationRoles),
            GitWorkspace.Discover(_repo.Root), executor);

    [Fact]
    public void Prepare_computes_everything_without_mutating_anything()
    {
        var executor = new FakeExecutor(_repo);
        var preview = Runner(executor).Prepare(null, null);

        Assert.Equal("recipe-tagging", preview.Plan.PlanId);
        Assert.Equal("01-tag-entity", preview.Checkpoint.Id);
        Assert.Equal(1, preview.Attempt);
        Assert.Equal("orca/recipe-tagging/01-tag-entity", preview.Branch);
        Assert.False(preview.BranchExists);
        Assert.False(preview.PendingDecision);

        // Prompt = plan section + acceptance criteria + child note body (spec §8).
        Assert.Contains("01-tag-entity: Add Tag entity + EF mapping", preview.Prompt);
        Assert.Contains("- Tag entity exists", preview.Prompt);
        Assert.Contains("Create the Tag entity exactly as the recipe pattern does.", preview.Prompt);
        Assert.Equal(CheckpointPrompt.Sha256(preview.Prompt), preview.PromptSha256);

        Assert.Equal(["Read", "Edit", "Bash(dotnet build*)"], preview.Options.AllowedTools);
        Assert.Contains("Bash(git push*)", preview.SettingsJson);
        Assert.Contains("--settings", preview.Argv);

        // Nothing moved: no branch, no .orca, still on main, tree clean.
        var workspace = GitWorkspace.Discover(_repo.Root);
        Assert.False(workspace.BranchExists(preview.Branch));
        Assert.Equal("main", workspace.CurrentBranch());
        Assert.False(Directory.Exists(Path.Combine(_repo.Root, ".orca")));
        Assert.Equal(0, executor.Invocations);
    }

    [Fact]
    public void Prepare_without_denied_tools_omits_the_settings_payload()
    {
        var preview = Runner(new FakeExecutor(_repo), deniedTools: []).Prepare(null, null);

        Assert.Null(preview.SettingsJson);
        Assert.Null(preview.Options.SettingsFilePath);
        Assert.DoesNotContain("--settings", preview.Argv);
    }

    [Fact]
    public async Task Run_creates_the_branch_writes_evidence_and_merges_run_state()
    {
        var executor = new FakeExecutor(_repo);
        var runner = Runner(executor);
        var preview = runner.Prepare(null, null);

        var outcome = await runner.RunAsync(preview);

        var workspace = GitWorkspace.Discover(_repo.Root);
        Assert.Equal(preview.Branch, workspace.CurrentBranch());
        Assert.Equal(1, executor.Invocations);
        Assert.Same(preview.Options, executor.LastOptions);
        Assert.Equal(preview.LogPaths.EventLog, executor.LastLogPaths!.EventLog);

        // The settings payload was on disk before the invocation saw its path.
        Assert.Equal(preview.SettingsJson, File.ReadAllText(preview.Options.SettingsFilePath!));

        Assert.Equal("fake-session-id", outcome.RunState.SessionId);
        Assert.Equal(0.25m, outcome.RunState.AccumulatedCostUsd);
        Assert.Equal(preview.PromptSha256, outcome.RunState.PromptHashes["implementer"]);
        // Record equality would compare the hash dictionary by reference; the JSON
        // projection is the durable contract anyway.
        Assert.Equal(RunStateSerializer.ToJson(outcome.RunState),
            RunStateSerializer.ToJson(new RunStateStore(_repo.Root).Read("recipe-tagging")!));
    }

    [Fact]
    public async Task Run_refuses_a_dirty_tree_before_anything_happens()
    {
        var executor = new FakeExecutor(_repo);
        var runner = Runner(executor);
        var preview = runner.Prepare(null, null);
        _repo.WriteFile("uncommitted.txt", "dirt\n");

        await Assert.ThrowsAsync<GitException>(() => runner.RunAsync(preview));
        Assert.Equal(0, executor.Invocations);
    }

    [Fact]
    public async Task Second_attempt_switches_to_the_existing_branch_and_accumulates_cost()
    {
        var executor = new FakeExecutor(_repo);
        var runner = Runner(executor);
        var first = await runner.RunAsync(runner.Prepare(null, null));
        runner.Approve(first.Preview, first.Execution.Result!.TotalCostUsd);

        var preview = runner.Prepare(null, "01-tag-entity");
        Assert.Equal(2, preview.Attempt);
        Assert.True(preview.BranchExists);

        var outcome = await runner.RunAsync(preview);
        Assert.Equal(2, executor.Invocations);
        Assert.Equal(0.50m, outcome.RunState.AccumulatedCostUsd);
    }

    [Fact]
    public void An_existing_branch_with_no_matching_state_is_refused()
    {
        var workspace = GitWorkspace.Discover(_repo.Root);
        workspace.CreateBranchFrom("orca/recipe-tagging/01-tag-entity", "main");
        workspace.SwitchTo("main");

        var e = Assert.Throws<CheckpointRunException>(() => Runner(new FakeExecutor(_repo)).Prepare(null, null));
        Assert.Contains("already exists", e.Message);
        // The branch may hold a human's in-progress work — Orca must never suggest
        // deleting it as the fix.
        Assert.DoesNotContain("delete the branch", e.Message);
    }

    [Fact]
    public async Task Rerun_after_an_intervening_checkpoint_derives_the_attempt_from_the_vault_not_a_reset()
    {
        var runner = Runner(new FakeExecutor(_repo));

        var firstA = await runner.RunAsync(runner.Prepare(null, "01-tag-entity"));
        runner.Approve(firstA.Preview, firstA.Execution.Result!.TotalCostUsd);
        // The Verifier's durable record for that attempt — the vault's memory of it —
        // outlives the run-state cache, which is about to be overwritten by checkpoint B.
        new Orca.Core.Vault.VaultWriter(new Orca.Core.Vault.VaultLayout(_vault, "Plans")).WriteVerificationRecord(
            new VerificationRecord(
                1, "recipe-tagging", 1, "01-tag-entity", 1, DateTimeOffset.Parse("2026-07-07T10:00:00Z"),
                new GateResults(GateOutcome.Pass, GateOutcome.Pass),
                [new SemanticCriterionResult("Tag entity exists", SemanticVerdict.Pass, null)],
                VerificationVerdict.Pass, "Looks good.\n"));

        var firstB = await runner.RunAsync(runner.Prepare(null, "02-endpoints"));
        runner.Approve(firstB.Preview, firstB.Execution.Result!.TotalCostUsd);
        // Run state now points at checkpoint B — it has no opinion left on checkpoint A.

        var secondA = runner.Prepare(null, "01-tag-entity");

        Assert.Equal(2, secondA.Attempt);
        Assert.True(secondA.BranchExists);
    }

    [Fact]
    public async Task Prepare_after_state_loss_renumbers_from_the_vault_instead_of_reusing_the_verified_attempt()
    {
        var runner = Runner(new FakeExecutor(_repo));

        var first = await runner.RunAsync(runner.Prepare(null, "01-tag-entity"));
        runner.Approve(first.Preview, first.Execution.Result!.TotalCostUsd);
        new Orca.Core.Vault.VaultWriter(new Orca.Core.Vault.VaultLayout(_vault, "Plans")).WriteVerificationRecord(
            new VerificationRecord(
                1, "recipe-tagging", 1, "01-tag-entity", 1, DateTimeOffset.Parse("2026-07-07T10:00:00Z"),
                new GateResults(GateOutcome.Pass, GateOutcome.Pass),
                [new SemanticCriterionResult("Tag entity exists", SemanticVerdict.Pass, null)],
                VerificationVerdict.Pass, "Looks good.\n"));

        // Simulate a second machine (or a wiped workspace): the local run-state cache is
        // gone, but the vault's evidence of attempt 1 survives.
        File.Delete(new RunStateStore(_repo.Root).PathFor("recipe-tagging"));

        var preview = runner.Prepare(null, "01-tag-entity");

        // Renumbered past the existing record, never reset to (and silently reusing) 1.
        Assert.Equal(2, preview.Attempt);
        Assert.True(preview.BranchExists);
    }

    [Fact]
    public async Task Prepare_refuses_when_state_and_vault_disagree_about_this_checkpoints_attempt()
    {
        var runner = Runner(new FakeExecutor(_repo));
        var first = await runner.RunAsync(runner.Prepare(null, "01-tag-entity"));
        runner.Approve(first.Preview, first.Execution.Result!.TotalCostUsd);

        // The vault already has evidence of attempts 1 *and* 2 for this checkpoint (as if
        // a second machine ran attempt 2 independently), but this machine's run state
        // still only knows about attempt 1.
        var writer = new Orca.Core.Vault.VaultWriter(new Orca.Core.Vault.VaultLayout(_vault, "Plans"));
        writer.WriteVerificationRecord(new VerificationRecord(
            1, "recipe-tagging", 1, "01-tag-entity", 1, DateTimeOffset.Parse("2026-07-07T10:00:00Z"),
            new GateResults(GateOutcome.Pass, GateOutcome.Pass),
            [new SemanticCriterionResult("Tag entity exists", SemanticVerdict.Pass, null)],
            VerificationVerdict.Pass, "Looks good.\n"));
        writer.WriteVerificationRecord(new VerificationRecord(
            1, "recipe-tagging", 1, "01-tag-entity", 2, DateTimeOffset.Parse("2026-07-08T10:00:00Z"),
            new GateResults(GateOutcome.Pass, GateOutcome.Pass),
            [new SemanticCriterionResult("Tag entity exists", SemanticVerdict.Pass, null)],
            VerificationVerdict.Pass, "Also fine.\n"));

        var e = Assert.Throws<CheckpointRunException>(() => runner.Prepare(null, "01-tag-entity"));

        Assert.Contains("attempt 2", e.Message);
        Assert.Contains("attempt 3", e.Message);
        Assert.Contains("second machine", e.Message);
        Assert.Contains(".orca/state", e.Message);
    }

    [Fact]
    public async Task An_undecided_attempt_is_flagged_and_blocks_a_new_invocation()
    {
        var executor = new FakeExecutor(_repo);
        var runner = Runner(executor);
        await runner.RunAsync(runner.Prepare(null, null));
        // Changes exist on the checkpoint branch; no approve/reject happened.

        var preview = runner.Prepare(null, "01-tag-entity");

        Assert.True(preview.PendingDecision);
        var e = await Assert.ThrowsAsync<CheckpointRunException>(() => runner.RunAsync(preview));
        Assert.Contains("awaiting", e.Message);
        Assert.Equal(1, executor.Invocations);
    }

    [Fact]
    public async Task Approve_commits_with_the_structured_message()
    {
        var runner = Runner(new FakeExecutor(_repo));
        var outcome = await runner.RunAsync(runner.Prepare(null, null));

        var sha = runner.Approve(outcome.Preview, outcome.Execution.Result!.TotalCostUsd);

        Assert.Matches("^[0-9a-f]{40}$", sha);
        var message = _repo.Git("log", "-1", "--format=%B");
        Assert.StartsWith("orca: recipe-tagging/01-tag-entity attempt 1", message);
        Assert.Contains("Session: fake-session-id", message);
        Assert.Contains("Cost: $0.25", message);
        Assert.True(GitWorkspace.Discover(_repo.Root).IsClean());
    }

    [Fact]
    public async Task Approve_refuses_when_not_on_the_checkpoint_branch()
    {
        var runner = Runner(new FakeExecutor(_repo));
        var outcome = await runner.RunAsync(runner.Prepare(null, null));
        // Simulate a human wandering off — commit the work so the switch is allowed,
        // then approve from main.
        _repo.Git("add", "-A");
        _repo.Git("commit", "-m", "manual commit outside orca");
        GitWorkspace.Discover(_repo.Root).SwitchTo("main");

        Assert.Throws<CheckpointRunException>(() => runner.Approve(outcome.Preview, null));
    }

    [Fact]
    public async Task Reject_without_discard_leaves_every_change_in_place()
    {
        var runner = Runner(new FakeExecutor(_repo));
        await runner.RunAsync(runner.Prepare(null, null));

        runner.Reject(discardChanges: false);

        Assert.False(GitWorkspace.Discover(_repo.Root).IsClean());
        Assert.True(File.Exists(Path.Combine(_repo.Root, "src/Tag.cs")));
    }

    [Fact]
    public async Task Reject_with_discard_restores_the_tree_but_keeps_run_evidence()
    {
        var runner = Runner(new FakeExecutor(_repo));
        var outcome = await runner.RunAsync(runner.Prepare(null, null));

        runner.Reject(discardChanges: true);

        Assert.True(GitWorkspace.Discover(_repo.Root).IsClean());
        Assert.False(File.Exists(Path.Combine(_repo.Root, "src/Tag.cs")));
        // Run state and the settings evidence survive: .orca/ is gitignored.
        Assert.NotNull(new RunStateStore(_repo.Root).Read("recipe-tagging"));
        Assert.True(File.Exists(outcome.Preview.Options.SettingsFilePath!));
    }

    [Fact]
    public void Prepare_refuses_a_draft_plan()
    {
        File.WriteAllText(
            Path.Combine(_vault, "Plans", "recipe-tagging", "recipe-tagging.md"),
            PlanText.Replace("status: approved", "status: draft"));

        var e = Assert.Throws<CheckpointRunException>(() => Runner(new FakeExecutor(_repo)).Prepare(null, null));
        Assert.Contains("draft", e.Message);
    }

    [Fact]
    public void Prepare_refuses_an_unknown_checkpoint()
    {
        var e = Assert.Throws<CheckpointRunException>(() =>
            Runner(new FakeExecutor(_repo)).Prepare(null, "09-nope"));
        Assert.Contains("09-nope", e.Message);
    }

    [Fact]
    public void Prepare_refuses_a_completed_checkpoint()
    {
        File.WriteAllText(
            Path.Combine(_vault, "Plans", "recipe-tagging", "recipe-tagging.md"),
            PlanText.Replace("id: 01-tag-entity\n    description: Add Tag entity + EF mapping\n    status: pending",
                "id: 01-tag-entity\n    description: Add Tag entity + EF mapping\n    status: complete"));

        var e = Assert.Throws<CheckpointRunException>(() =>
            Runner(new FakeExecutor(_repo)).Prepare(null, "01-tag-entity"));
        Assert.Contains("complete", e.Message);
    }

    [Fact]
    public void Prepare_with_no_pending_checkpoints_says_so()
    {
        File.WriteAllText(
            Path.Combine(_vault, "Plans", "recipe-tagging", "recipe-tagging.md"),
            PlanText.Replace("status: pending", "status: complete"));

        var e = Assert.Throws<CheckpointRunException>(() => Runner(new FakeExecutor(_repo)).Prepare(null, null));
        Assert.Contains("no pending checkpoints", e.Message);
    }

    [Fact]
    public void Prepare_appends_the_latest_findings_to_the_prompt()
    {
        // Prior findings live in the vault (spec §8: the Reflexion memory), possibly
        // from different attempts — a gate-failed attempt has no reconciliation.
        var writer = new Orca.Core.Vault.VaultWriter(new Orca.Core.Vault.VaultLayout(_vault, "Plans"));
        writer.WriteVerificationRecord(new VerificationRecord(
            1, "recipe-tagging", 1, "01-tag-entity", 1, DateTimeOffset.Parse("2026-07-07T10:00:00Z"),
            new GateResults(GateOutcome.Pass, GateOutcome.Pass),
            [
                new SemanticCriterionResult("Tag entity exists", SemanticVerdict.Pass, null),
                new SemanticCriterionResult("EF mapping compiles", SemanticVerdict.Fail, "no DbSet in the diff"),
            ],
            VerificationVerdict.Fail,
            "## Gates\n\n- build: pass\n- tests: pass\n"));
        writer.WriteReconciliationRecord(new ReconciliationRecord(
            1, "recipe-tagging", 1, "01-tag-entity", 1,
            ReconciliationVerdict.Partial, ["the EF mapping the plan asks for"], [], "Entity only.\n"));

        var preview = Runner(new FakeExecutor(_repo)).Prepare(null, "01-tag-entity");

        Assert.Contains("## Previous attempt findings", preview.Prompt);
        Assert.Contains("Verification of attempt 1 — verdict: fail", preview.Prompt);
        Assert.Contains("[fail] EF mapping compiles — no DbSet in the diff", preview.Prompt);
        Assert.Contains("Reconciliation of attempt 1 — implements_plan: partial", preview.Prompt);
        Assert.Contains("the EF mapping the plan asks for", preview.Prompt);
    }

    [Fact]
    public void A_first_attempt_prompt_carries_no_findings_section()
    {
        var preview = Runner(new FakeExecutor(_repo)).Prepare(null, null);

        Assert.DoesNotContain("Previous attempt findings", preview.Prompt);
    }

    [Fact]
    public void Prepare_refuses_an_attempt_beyond_the_ceiling()
    {
        new RunStateStore(_repo.Root).Write(new RunState
        {
            SchemaVersion = 1,
            PlanId = "recipe-tagging",
            PlanVersion = 1,
            CheckpointIndex = 0,
            CheckpointId = "01-tag-entity",
            Attempt = 3,
            Branch = "orca/recipe-tagging/01-tag-entity",
            SessionId = null,
            PromptHashes = new Dictionary<string, string>(),
            AccumulatedCostUsd = 0m,
            LastUsage = null,
            LastStopReason = null,
            StartedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        _repo.Git("branch", "orca/recipe-tagging/01-tag-entity");

        var e = Assert.Throws<CheckpointRunException>(() =>
            Runner(new FakeExecutor(_repo), maxCheckpointAttempts: 3).Prepare(null, "01-tag-entity"));

        Assert.Contains("maxCheckpointAttempts=3", e.Message);
    }

    [Fact]
    public void Prepare_refuses_a_repo_not_configured_for_verification()
    {
        // Fail before the Implementer spends anything, naming what is missing.
        var noRoles = Assert.Throws<ConfigException>(() =>
            Runner(new FakeExecutor(_repo), verificationRoles: false).Prepare(null, null));
        Assert.Contains("reconciler", noRoles.Message);

        var noParseRetries = Assert.Throws<ConfigException>(() =>
            Runner(new FakeExecutor(_repo), maxParseRetries: null).Prepare(null, null));
        Assert.Contains("maxParseRetries", noParseRetries.Message);

        var noCeiling = Assert.Throws<ConfigException>(() =>
            Runner(new FakeExecutor(_repo), maxCheckpointAttempts: null).Prepare(null, null));
        Assert.Contains("maxCheckpointAttempts", noCeiling.Message);
    }

    /// <summary>Stands in for the claude process: records the invocation, drops a file
    /// into the target repo the way a real Implementer would, and returns a canned
    /// success. No process, no quota.</summary>
    private sealed class FakeExecutor(TempGitRepo repo) : IClaudeExecutor
    {
        public int Invocations { get; private set; }
        public ClaudeInvocationOptions? LastOptions { get; private set; }
        public RunLogPaths? LastLogPaths { get; private set; }

        public Task<ClaudeExecutionResult> ExecuteAsync(
            ClaudeInvocationOptions options, RunLogPaths logPaths,
            IClaudeStreamObserver? observer = null, CancellationToken cancellationToken = default)
        {
            Invocations++;
            LastOptions = options;
            LastLogPaths = logPaths;
            repo.WriteFile("src/Tag.cs", "public sealed record Tag(string Name);\n");
            return Task.FromResult(new ClaudeExecutionResult
            {
                Outcome = ExecutionOutcome.Completed,
                ExitCode = 0,
                SessionId = "fake-session-id",
                Result = new ClaudeResultSummary
                {
                    Subtype = "success",
                    IsError = false,
                    SessionId = "fake-session-id",
                    NumTurns = 3,
                    DurationMs = 1200,
                    TotalCostUsd = 0.25m,
                    Usage = new TokenUsage
                    {
                        InputTokens = 100,
                        OutputTokens = 50,
                        CacheCreationInputTokens = 0,
                        CacheReadInputTokens = 0,
                    },
                },
                Elapsed = TimeSpan.FromSeconds(1.2),
            });
        }
    }
}
