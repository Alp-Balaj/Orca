using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Run;

namespace Orca.Tests;

/// <summary>SUG-03 stage B: checkpoint execution in a linked worktree — a private tree
/// per lane, with run state, run logs, and the branch shared through the main repo.</summary>
public sealed class WorktreeExecutionTests : IDisposable
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
        ---
        Plan body.
        """;

    private readonly TempGitRepo _repo = new();
    private readonly string _vault =
        Path.Combine(Path.GetTempPath(), "orca-vault-tests-" + Guid.NewGuid().ToString("N"));

    public WorktreeExecutionTests()
    {
        var planDir = Path.Combine(_vault, "Plans", "recipe-tagging");
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "recipe-tagging.md"), PlanText);

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
        MaxParseRetries = 1,
        MaxCheckpointAttempts = 3,
        PlanningCandidates = null,
        DeniedTools = ["Bash(git push*)"],
        RoleSections = new RolesSection
        {
            Implementer = new RoleSection
            {
                AllowedTools = ["Read", "Edit"],
                MaxTurns = 10,
                TimeoutSeconds = 300,
            },
            Reconciler = new RoleSection { AllowedTools = ["Read", "Write"], MaxTurns = 10, TimeoutSeconds = 300 },
            Verifier = new RoleSection { AllowedTools = ["Read", "Write"], MaxTurns = 10, TimeoutSeconds = 300 },
        },
    };

    private string WorktreePath => Path.Combine(_repo.Root, ".orca", "worktrees", "recipe-tagging", "01-tag-entity");

    // --- GitWorkspace worktree mechanics ---

    [Fact]
    public void Detached_worktree_roundtrip_add_recognize_remove()
    {
        var workspace = GitWorkspace.Discover(_repo.Root);

        Assert.False(workspace.IsRegisteredWorktree(WorktreePath));
        workspace.AddWorktreeDetached(WorktreePath, "main");
        Assert.True(workspace.IsRegisteredWorktree(WorktreePath));
        Assert.True(File.Exists(Path.Combine(WorktreePath, "README.md")));

        workspace.RemoveWorktree(WorktreePath);
        Assert.False(workspace.IsRegisteredWorktree(WorktreePath));
        Assert.False(Directory.Exists(WorktreePath));
    }

    [Fact]
    public void A_worktree_for_an_existing_branch_checks_it_out_there()
    {
        _repo.Git("branch", "orca/recipe-tagging/01-tag-entity", "main");
        var workspace = GitWorkspace.Discover(_repo.Root);

        workspace.AddWorktreeForBranch(WorktreePath, "orca/recipe-tagging/01-tag-entity");

        var inWorktree = GitWorkspace.Discover(WorktreePath);
        Assert.Equal("orca/recipe-tagging/01-tag-entity", inWorktree.CurrentBranch());
        // The structural guard: the same branch cannot be checked out in a second tree.
        Assert.Throws<GitException>(() =>
            workspace.AddWorktreeForBranch(WorktreePath + "-second", "orca/recipe-tagging/01-tag-entity"));
    }

    [Fact]
    public void Removing_a_dirty_worktree_refuses_instead_of_discarding_work()
    {
        var workspace = GitWorkspace.Discover(_repo.Root);
        workspace.AddWorktreeDetached(WorktreePath, "main");
        File.WriteAllText(Path.Combine(WorktreePath, "undecided.txt"), "not yet reviewed\n");

        Assert.Throws<GitException>(() => workspace.RemoveWorktree(WorktreePath));
        Assert.True(File.Exists(Path.Combine(WorktreePath, "undecided.txt")));
    }

    // --- plan-scoped locking ---

    [Fact]
    public void Two_plans_lock_independently_but_one_plan_refuses_a_second_holder()
    {
        using var planA = OrcaLock.Acquire(_repo.Root, scope: "plan-a");
        using var planB = OrcaLock.Acquire(_repo.Root, scope: "plan-b");
        using var repoWide = OrcaLock.Acquire(_repo.Root);

        var ex = Assert.Throws<CheckpointRunException>(() => OrcaLock.Acquire(_repo.Root, scope: "plan-a"));

        Assert.Contains("plan 'plan-a'", ex.Message);
        Assert.True(File.Exists(OrcaLock.PathFor(_repo.Root, "plan-a")));
        Assert.True(File.Exists(OrcaLock.PathFor(_repo.Root)));
    }

    [Fact]
    public void A_scope_that_could_escape_the_lock_directory_is_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => OrcaLock.PathFor(_repo.Root, "../evil"));
    }

    // --- checkpoint execution in the worktree, evidence in the main repo ---

    [Fact]
    public async Task A_worktree_run_mutates_only_the_worktree_and_anchors_state_to_the_main_repo()
    {
        var main = GitWorkspace.Discover(_repo.Root);
        main.AddWorktreeDetached(WorktreePath, "main");
        var worktree = GitWorkspace.Discover(WorktreePath);
        var runner = new CheckpointRunner(
            Config(), worktree, new WorktreeFakeExecutor(), stateRepoRoot: _repo.Root);

        var preview = runner.Prepare(null, null);
        Assert.Equal(WorktreePath, preview.Options.WorkingDirectory);
        Assert.StartsWith(Path.Combine(_repo.Root, ".orca", "runs"), preview.LogPaths.EventLog);

        var outcome = await runner.RunAsync(preview);

        // The checkpoint branch was created inside the worktree; the main tree never moved.
        Assert.Equal(preview.Branch, worktree.CurrentBranch());
        Assert.Equal("main", main.CurrentBranch());
        Assert.True(main.IsClean());
        Assert.False(worktree.IsClean());
        Assert.True(File.Exists(Path.Combine(WorktreePath, "src", "Tag.cs")));
        Assert.False(File.Exists(Path.Combine(_repo.Root, "src", "Tag.cs")));

        // Run state lives in the main repo's .orca, not the worktree's (which dies with it).
        Assert.NotNull(new RunStateStore(_repo.Root).Read("recipe-tagging"));
        Assert.False(Directory.Exists(Path.Combine(WorktreePath, ".orca", "state")));
        Assert.Equal(InvocationStopReason.Succeeded, outcome.Execution.StopReason);
    }

    [Fact]
    public async Task Approving_in_the_worktree_commits_the_branch_visible_from_the_main_repo()
    {
        var main = GitWorkspace.Discover(_repo.Root);
        main.AddWorktreeDetached(WorktreePath, "main");
        var worktree = GitWorkspace.Discover(WorktreePath);
        var runner = new CheckpointRunner(
            Config(), worktree, new WorktreeFakeExecutor(), stateRepoRoot: _repo.Root);

        var preview = runner.Prepare(null, null);
        await runner.RunAsync(preview);
        var sha = runner.Approve(preview, attemptCostUsd: 0.25m);

        Assert.True(worktree.IsClean());
        Assert.True(main.BranchExists(preview.Branch));
        Assert.Equal(sha, _repo.Git("rev-parse", preview.Branch).Trim());
        Assert.StartsWith("orca: recipe-tagging/01-tag-entity attempt 1",
            main.CommitSubject(preview.Branch));

        // Clean after commit — the CLI's lifecycle rule may now remove the tree; the
        // branch and its commit survive in the shared repo.
        main.RemoveWorktree(WorktreePath);
        Assert.True(main.BranchExists(preview.Branch));
    }

    /// <summary>Writes into the invocation's working directory — which in a worktree run
    /// must be the worktree, never the main tree.</summary>
    private sealed class WorktreeFakeExecutor : IClaudeExecutor
    {
        public Task<ClaudeExecutionResult> ExecuteAsync(
            ClaudeInvocationOptions options, RunLogPaths logPaths,
            IClaudeStreamObserver? observer = null, CancellationToken cancellationToken = default)
        {
            var target = Path.Combine(options.WorkingDirectory, "src", "Tag.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "public sealed record Tag(string Name);\n");
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
