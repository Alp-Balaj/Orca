using Orca.Core.Git;

namespace Orca.Tests;

public sealed class GitWorkspaceTests : IDisposable
{
    private readonly TempGitRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    private GitWorkspace Workspace() => GitWorkspace.Discover(_repo.Root);

    [Fact]
    public void Discover_finds_the_toplevel_from_a_subdirectory()
    {
        var sub = Path.Combine(_repo.Root, "src", "deep");
        Directory.CreateDirectory(sub);

        var workspace = GitWorkspace.Discover(sub);

        // Realpath both sides: macOS/Linux temp dirs are often symlinked.
        Assert.Equal(new DirectoryInfo(_repo.Root).FullName,
            new DirectoryInfo(workspace.RepoRoot).FullName);
    }

    [Fact]
    public void Discover_refuses_a_directory_outside_any_repo()
    {
        var outside = Path.Combine(Path.GetTempPath(), "orca-not-a-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Assert.Throws<GitException>(() => GitWorkspace.Discover(outside));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Clean_tree_is_clean_and_untracked_files_count_as_dirty()
    {
        var workspace = Workspace();
        Assert.True(workspace.IsClean());

        _repo.WriteFile("new-file.txt", "untracked\n");
        Assert.False(workspace.IsClean());
        Assert.Contains(workspace.StatusLines(), line => line.Contains("new-file.txt"));
    }

    [Fact]
    public void Creates_the_checkpoint_branch_from_the_base_and_switches_to_it()
    {
        var workspace = Workspace();

        workspace.CreateBranchFrom("orca/recipe-tagging/01-tag-entity", "main");

        Assert.Equal("orca/recipe-tagging/01-tag-entity", workspace.CurrentBranch());
        Assert.True(workspace.BranchExists("orca/recipe-tagging/01-tag-entity"));
    }

    [Fact]
    public void Refuses_to_branch_from_a_dirty_tree()
    {
        _repo.WriteFile("dirty.txt", "x\n");

        var e = Assert.Throws<GitException>(() =>
            Workspace().CreateBranchFrom("orca/p/01-c", "main"));
        Assert.Contains("not clean", e.Message);
    }

    [Fact]
    public void Refuses_a_missing_base_branch()
    {
        var e = Assert.Throws<GitException>(() =>
            Workspace().CreateBranchFrom("orca/p/01-c", "does-not-exist"));
        Assert.Contains("does-not-exist", e.Message);
    }

    [Fact]
    public void Refuses_to_recreate_an_existing_branch()
    {
        var workspace = Workspace();
        workspace.CreateBranchFrom("orca/p/01-c", "main");
        workspace.SwitchTo("main");

        var e = Assert.Throws<GitException>(() => workspace.CreateBranchFrom("orca/p/01-c", "main"));
        Assert.Contains("already exists", e.Message);
    }

    [Fact]
    public void Commit_on_approval_stages_everything_and_carries_the_message()
    {
        var workspace = Workspace();
        workspace.CreateBranchFrom("orca/p/01-c", "main");
        _repo.WriteFile("src/Feature.cs", "// new\n");
        _repo.WriteFile("README.md", "seed\nchanged\n");

        var message = "orca: p/01-c attempt 1\n\nPlan: p v1";
        var sha = workspace.StageAllAndCommit(message);

        Assert.True(workspace.IsClean());
        Assert.Matches("^[0-9a-f]{40}$", sha);
        Assert.StartsWith("orca: p/01-c attempt 1", _repo.Git("log", "-1", "--format=%B"));
    }

    [Fact]
    public void Commit_with_nothing_to_commit_fails_loudly()
    {
        var e = Assert.Throws<GitException>(() => Workspace().StageAllAndCommit("orca: x"));
        Assert.Contains("nothing to commit", e.Message);
    }

    [Fact]
    public void TryFastForwardMerge_advances_the_base_branch_when_possible()
    {
        var workspace = Workspace();
        workspace.CreateBranchFrom("orca/p/01-c", "main");
        _repo.WriteFile("src/Feature.cs", "// new\n");
        var sha = workspace.StageAllAndCommit("orca: p/01-c attempt 1");

        var merged = workspace.TryFastForwardMerge("orca/p/01-c", "main");

        Assert.True(merged);
        Assert.Equal("main", workspace.CurrentBranch());
        Assert.Equal(sha, _repo.Git("rev-parse", "main").Trim());
        Assert.True(File.Exists(Path.Combine(_repo.Root, "src/Feature.cs")));
    }

    [Fact]
    public void TryFastForwardMerge_refuses_without_altering_history_when_base_has_diverged()
    {
        var workspace = Workspace();
        workspace.CreateBranchFrom("orca/p/01-c", "main");
        _repo.WriteFile("src/Feature.cs", "// new\n");
        var checkpointSha = workspace.StageAllAndCommit("orca: p/01-c attempt 1");

        // main moves on independently — a second machine, or the human committing
        // something else — before the human confirms "run next now?".
        workspace.SwitchTo("main");
        _repo.WriteFile("README.md", "seed\ndiverged on main\n");
        var mainSha = workspace.StageAllAndCommit("unrelated change on main");

        var merged = workspace.TryFastForwardMerge("orca/p/01-c", "main");

        Assert.False(merged);
        Assert.Equal("main", workspace.CurrentBranch());
        Assert.Equal(mainSha, _repo.Git("rev-parse", "main").Trim());
        Assert.Equal(checkpointSha, _repo.Git("rev-parse", "orca/p/01-c").Trim());
        Assert.True(workspace.IsClean());
    }

    [Fact]
    public void Discard_removes_tracked_modifications_and_untracked_files()
    {
        var workspace = Workspace();
        workspace.CreateBranchFrom("orca/p/01-c", "main");
        _repo.WriteFile("README.md", "tracked modification\n");
        _repo.WriteFile("junk/new.txt", "untracked\n");

        workspace.DiscardAllChanges();

        Assert.True(workspace.IsClean());
        Assert.Equal("seed\n", File.ReadAllText(Path.Combine(_repo.Root, "README.md")));
        Assert.False(File.Exists(Path.Combine(_repo.Root, "junk/new.txt")));
    }

    [Fact]
    public void Discard_leaves_gitignored_run_evidence_alone()
    {
        // The rejection path must never delete run logs: .orca/ is ignored, and
        // `git clean -fd` (no -x) skips ignored files.
        var workspace = Workspace();
        _repo.WriteFile(".gitignore", ".orca/\n");
        _repo.Git("add", "-A");
        _repo.Git("commit", "-m", "ignore .orca");
        _repo.WriteFile(".orca/runs/p/01-c.attempt-1.ndjson", "{\"type\":\"system\"}\n");
        _repo.WriteFile("junk.txt", "discard me\n");

        workspace.DiscardAllChanges();

        Assert.True(File.Exists(Path.Combine(_repo.Root, ".orca/runs/p/01-c.attempt-1.ndjson")));
        Assert.False(File.Exists(Path.Combine(_repo.Root, "junk.txt")));
    }

    [Fact]
    public void Switch_refuses_a_dirty_tree()
    {
        var workspace = Workspace();
        workspace.CreateBranchFrom("orca/p/01-c", "main");
        _repo.WriteFile("wip.txt", "x\n");

        Assert.Throws<GitException>(() => workspace.SwitchTo("main"));
    }

    [Fact]
    public void FullDiff_covers_tracked_changes_and_untracked_content()
    {
        var workspace = Workspace();
        _repo.WriteFile(".gitignore", ".orca/\n");
        _repo.Git("add", "-A");
        _repo.Git("commit", "-m", "ignore .orca");

        _repo.WriteFile("README.md", "seed\nplus a change\n");
        _repo.WriteFile("nested/new.txt", "brand new content\n");
        File.WriteAllBytes(Path.Combine(_repo.Root, "blob.bin"), [0x00, 0x01, 0x02]);
        _repo.WriteFile(".orca/runs/p/log.ndjson", "{}\n"); // ignored — must not appear

        var diff = workspace.FullDiff();

        Assert.Contains("+plus a change", diff);
        Assert.Contains("## New file (untracked): nested/new.txt", diff);
        Assert.Contains("brand new content", diff);
        Assert.Contains("## New file (untracked): blob.bin", diff);
        Assert.Contains("(binary file, 3 bytes — content omitted)", diff);
        Assert.DoesNotContain(".orca/runs", diff);
    }

    [Fact]
    public void FullDiff_marks_a_nested_repo_directory_entry_without_reading_it_as_a_file()
    {
        var workspace = Workspace();
        var nestedRepoPath = Path.Combine(_repo.Root, "vendor", "nested");
        Directory.CreateDirectory(nestedRepoPath);
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = nestedRepoPath,
            ArgumentList = { "init", "-b", "main" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using (var process = System.Diagnostics.Process.Start(startInfo)!)
            process.WaitForExit();

        var diff = workspace.FullDiff();

        Assert.Contains("## New file (untracked): vendor/nested/", diff);
        Assert.Contains("(directory entry — content omitted)", diff);
    }

    [Fact]
    public void FullDiff_renders_a_broken_symlink_as_unreadable_instead_of_throwing()
    {
        var workspace = Workspace();
        var linkPath = Path.Combine(_repo.Root, "broken-link");
        try
        {
            File.CreateSymbolicLink(linkPath, Path.Combine(_repo.Root, "does-not-exist-target"));
        }
        catch (IOException)
        {
            return; // No SeCreateSymbolicLinkPrivilege (Windows without admin/Developer Mode).
        }

        var diff = workspace.FullDiff();

        Assert.Contains("## New file (untracked): broken-link", diff);
        Assert.Contains("(unreadable: broken-link:", diff);
    }

    [Fact]
    public void FullDiff_renders_a_permission_denied_file_as_unreadable_instead_of_throwing()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return; // Unix file modes only.

        var workspace = Workspace();
        _repo.WriteFile("locked.txt", "secret\n");
        var path = Path.Combine(_repo.Root, "locked.txt");
        File.SetUnixFileMode(path, UnixFileMode.None);

        try
        {
            var diff = workspace.FullDiff();

            Assert.Contains("## New file (untracked): locked.txt", diff);
            Assert.Contains("(unreadable: locked.txt:", diff);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void FullDiff_truncates_a_single_untracked_file_past_the_per_file_budget()
    {
        var workspace = Workspace();
        var oversized = new string('a', 64 * 1024 + 500);
        _repo.WriteFile("big.txt", oversized);

        var diff = workspace.FullDiff();

        Assert.Contains("## New file (untracked): big.txt", diff);
        Assert.Contains("(truncated, 500 bytes omitted — see git)", diff);
        Assert.DoesNotContain(new string('a', 64 * 1024 + 1), diff);
    }

    [Fact]
    public void FullDiff_replaces_further_untracked_files_once_the_total_budget_is_exhausted()
    {
        var workspace = Workspace();
        // Four files at exactly the per-file budget exhaust the 256 KiB total budget.
        for (var i = 0; i < 4; i++)
            _repo.WriteFile($"chunk-{i}.txt", new string((char)('a' + i), 64 * 1024));
        _repo.WriteFile("overflow.txt", "should not be embedded\n");

        var diff = workspace.FullDiff();

        Assert.Contains("## New file (untracked): overflow.txt", diff);
        Assert.Contains("(truncated, untracked-content budget exhausted — see git)", diff);
        Assert.DoesNotContain("should not be embedded", diff);
    }

    [Fact]
    public void WorkingTreeHash_is_deterministic_and_content_sensitive()
    {
        var workspace = Workspace();
        _repo.WriteFile("README.md", "seed\nchanged\n");

        var first = workspace.WorkingTreeHash();
        var second = workspace.WorkingTreeHash();
        Assert.Equal(first, second);

        _repo.WriteFile("README.md", "seed\nchanged differently\n");
        Assert.NotEqual(first, workspace.WorkingTreeHash());
    }

    [Fact]
    public void WorkingTreeHash_is_well_defined_on_a_clean_tree()
    {
        var workspace = Workspace();
        Assert.True(workspace.IsClean());

        var hash = workspace.WorkingTreeHash();

        Assert.Equal(_repo.Git("rev-parse", "HEAD^{tree}").Trim(), hash);
    }

    [Fact]
    public void WorkingTreeHash_covers_an_untracked_file_inside_a_new_untracked_directory()
    {
        // Regression case: `git stash create -u` (tried first) silently drops untracked
        // files that live inside an untracked *directory* on git 2.54 — exactly what an
        // Implementer attempt normally produces (a new file in a new folder), which is an
        // ordinary case, not an edge case. Confirmed empirically before picking the
        // GIT_INDEX_FILE-based implementation actually shipped.
        var workspace = Workspace();
        var before = workspace.WorkingTreeHash();

        _repo.WriteFile("src/New/Nested.cs", "// brand new, nested, untracked\n");

        Assert.NotEqual(before, workspace.WorkingTreeHash());
    }

    [Fact]
    public void WorkingTreeHash_never_touches_the_real_index_or_working_tree()
    {
        var workspace = Workspace();
        _repo.WriteFile("README.md", "seed\nchanged\n");
        _repo.WriteFile("untracked/new.txt", "new\n");
        var statusBefore = workspace.StatusLines();

        workspace.WorkingTreeHash();

        Assert.Equal(statusBefore, workspace.StatusLines());
        Assert.Empty(_repo.Git("diff", "--cached", "--name-only")); // real index still empty
    }

    [Fact]
    public void FullDiff_preserves_binary_detection_for_untracked_files()
    {
        var workspace = Workspace();
        File.WriteAllBytes(Path.Combine(_repo.Root, "asset.bin"), [0x00, 0xFF, 0x10, 0x20]);

        var diff = workspace.FullDiff();

        Assert.Contains("## New file (untracked): asset.bin", diff);
        Assert.Contains("(binary file, 4 bytes — content omitted)", diff);
    }
}
