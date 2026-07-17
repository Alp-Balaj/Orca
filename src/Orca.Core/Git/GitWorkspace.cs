namespace Orca.Core.Git;

/// <summary>Deterministic git mechanics over the target repo (spec §7.1). Orca — never
/// the agent — creates checkpoint branches, commits on human approval, and discards on
/// confirmed rejection; the agent's allowlist carries no git write commands at all. Git
/// is the sandbox layer the whole design leans on hardest: nothing reaches the base
/// branch without a human reviewing the diff.</summary>
public sealed class GitWorkspace
{
    /// <summary>Per-untracked-file embed cap. Past this, a file's content is truncated
    /// with a marker naming the omitted byte count — one runaway fixture or generated
    /// asset must not blow out the reviewable diff.</summary>
    private const int PerFileByteBudget = 64 * 1024;

    /// <summary>Cumulative cap across all untracked files' embedded content. Once
    /// exceeded, remaining untracked files are replaced by a marker rather than
    /// embedded at all.</summary>
    private const int TotalUntrackedByteBudget = 256 * 1024;

    /// <summary>Hard ceiling on the whole returned text (tracked diff + untracked
    /// embeds + markers). Belt-and-braces against the tracked `git diff HEAD` output
    /// itself being enormous.</summary>
    private const int TotalOutputByteBudget = 1024 * 1024;

    private GitWorkspace(string repoRoot) => RepoRoot = repoRoot;

    public string RepoRoot { get; }

    /// <summary>Opens the work tree containing <paramref name="startDirectory"/>, rooted
    /// where git says the toplevel is — commands may run from anywhere inside the repo,
    /// but paths like <c>orca.json</c> and <c>.orca/</c> anchor to the root.</summary>
    public static GitWorkspace Discover(string startDirectory)
    {
        if (!Directory.Exists(startDirectory))
            throw new GitException($"directory does not exist: {startDirectory}");

        var result = GitProcess.Run(startDirectory, ["rev-parse", "--show-toplevel"], allowNonZero: true);
        if (result.ExitCode != 0)
            throw new GitException(
                $"{startDirectory} is not inside a git work tree: {result.Stderr.Trim()}");

        return new GitWorkspace(Path.GetFullPath(result.Stdout.Trim()));
    }

    /// <summary>True when <c>git status --porcelain</c> reports nothing. Untracked files
    /// count as dirty — a checkpoint must start from a state git can fully restore, and
    /// untracked files are exactly what a hard reset cannot undo.</summary>
    public bool IsClean() => StatusLines().Count == 0;

    /// <summary>Porcelain status lines — the concise, complete change summary shown at
    /// the approval prompt (covers untracked files, which a diff stat misses).</summary>
    public IReadOnlyList<string> StatusLines() =>
        GitProcess.Run(RepoRoot, ["status", "--porcelain"]).Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Diffstat of tracked changes against HEAD, for the human's approval view.</summary>
    public string DiffStat() =>
        GitProcess.Run(RepoRoot, ["diff", "--stat", "HEAD"]).Stdout.TrimEnd();

    /// <summary>The complete uncommitted change set as reviewable text — what the
    /// Reconciler and Verifier judge: <c>git diff HEAD</c> (tracked, staged + unstaged)
    /// plus the full content of untracked files, which a plain diff omits. Untracked
    /// paths come from <c>status --porcelain -z -uall</c> (NUL-separated, so no quoting
    /// ambiguity; <c>-uall</c> lists files inside untracked directories individually).</summary>
    public string FullDiff()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(GitProcess.Run(RepoRoot, ["diff", "HEAD"]).Stdout);

        var status = GitProcess.Run(RepoRoot, ["status", "--porcelain", "-z", "-uall"]).Stdout;
        var untrackedTotal = 0;
        foreach (var entry in status.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!entry.StartsWith("?? ", StringComparison.Ordinal))
                continue;

            var path = entry[3..];
            sb.Append($"\n## New file (untracked): {path}\n\n");

            // `-uall` still reports a nested repo/submodule or an otherwise-untraversable
            // directory as a single "?? path/" entry — reading it as a file would throw.
            if (path.EndsWith('/'))
            {
                sb.Append("(directory entry — content omitted)\n");
                continue;
            }

            if (untrackedTotal >= TotalUntrackedByteBudget)
            {
                sb.Append("(truncated, untracked-content budget exhausted — see git)\n");
                continue;
            }

            var fullPath = Path.Combine(RepoRoot, path.Replace('/', Path.DirectorySeparatorChar));
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                sb.Append($"(unreadable: {path}: {ex.Message})\n");
                continue;
            }

            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0)
            {
                sb.Append($"(binary file, {bytes.Length} bytes — content omitted)\n");
                continue;
            }

            var remaining = TotalUntrackedByteBudget - untrackedTotal;
            var embedLength = Math.Min(bytes.Length, Math.Min(PerFileByteBudget, remaining));
            sb.Append(System.Text.Encoding.UTF8.GetString(bytes, 0, embedLength)).Append('\n');
            untrackedTotal += embedLength;

            var omitted = bytes.Length - embedLength;
            if (omitted > 0)
                sb.Append($"(truncated, {omitted} bytes omitted — see git)\n");
        }

        if (sb.Length > TotalOutputByteBudget)
        {
            sb.Length = TotalOutputByteBudget;
            sb.Append("\n(truncated, output budget exhausted — see git)\n");
        }

        return sb.ToString();
    }

    /// <summary>Content fingerprint of the current working tree (tracked + untracked,
    /// respecting .gitignore — the same universe <see cref="StageAllAndCommit"/> commits),
    /// independent of commit metadata like timestamps: two calls over identical content
    /// always agree, and any content change anywhere changes it. A clean tree fingerprints
    /// to HEAD's own tree, so this is well-defined whether or not anything is uncommitted.
    /// Builds the tree via a scratch index (<c>GIT_INDEX_FILE</c> pointed at a throwaway
    /// file) seeded from HEAD then `add -A`'d — a fact-finding read that never touches the
    /// repo's real index or working tree. (<c>git stash create -u</c> was tried first and
    /// rejected: on git 2.54, it silently drops untracked files that live inside an
    /// untracked *directory* — exactly the shape of "Implementer adds a new file in a new
    /// folder" — which would have made this fingerprint wrong in an ordinary case, not an
    /// edge case.)</summary>
    public string WorkingTreeHash()
    {
        var scratchIndex = Path.Combine(Path.GetTempPath(), $"orca-index-{Guid.NewGuid():N}");
        try
        {
            var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = scratchIndex };
            GitProcess.Run(RepoRoot, ["read-tree", "HEAD"], environment: env);
            GitProcess.Run(RepoRoot, ["add", "-A"], environment: env);
            return GitProcess.Run(RepoRoot, ["write-tree"], environment: env).Stdout.Trim();
        }
        finally
        {
            File.Delete(scratchIndex);
        }
    }

    public string CurrentBranch()
    {
        var branch = GitProcess.Run(RepoRoot, ["rev-parse", "--abbrev-ref", "HEAD"]).Stdout.Trim();
        if (branch == "HEAD")
            throw new GitException("HEAD is detached — check out a branch before running Orca");
        return branch;
    }

    public bool BranchExists(string branch) =>
        GitProcess.Run(RepoRoot, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], allowNonZero: true)
            .ExitCode == 0;

    /// <summary>The subject line (first line) of the commit at <paramref name="ref"/> —
    /// used to recognize Orca's own structured approval commits (see
    /// <see cref="CheckpointCommitMessage"/>) without parsing the full body. Null when the
    /// ref does not resolve to a commit.</summary>
    public string? CommitSubject(string @ref)
    {
        var result = GitProcess.Run(RepoRoot, ["log", "-1", "--format=%s", @ref], allowNonZero: true);
        return result.ExitCode == 0 ? result.Stdout.TrimEnd('\r', '\n') : null;
    }

    /// <summary>True when <paramref name="ancestor"/> is an ancestor of
    /// <paramref name="descendant"/> (<c>git merge-base --is-ancestor</c>) — a read-only
    /// history check, e.g. whether an approved checkpoint branch has already been merged
    /// into the plan's base branch.</summary>
    public bool IsAncestor(string ancestor, string descendant)
    {
        var result = GitProcess.Run(
            RepoRoot, ["merge-base", "--is-ancestor", ancestor, descendant], allowNonZero: true);
        if (result.ExitCode is 0 or 1)
            return result.ExitCode == 0;
        throw new GitException(
            $"git merge-base --is-ancestor {ancestor} {descendant} exited {result.ExitCode}: {result.Stderr.Trim()}");
    }

    /// <summary>True when git itself would ignore <paramref name="relativePath"/>
    /// (<c>git check-ignore -q</c>) — the authoritative answer, unlike any textual scan
    /// of .gitignore (SUG-10 / the BF-17 paper cut).</summary>
    public bool IsPathIgnored(string relativePath)
    {
        var result = GitProcess.Run(
            RepoRoot, ["check-ignore", "-q", "--", relativePath], allowNonZero: true);
        if (result.ExitCode is 0 or 1)
            return result.ExitCode == 0;
        throw new GitException(
            $"git check-ignore {relativePath} exited {result.ExitCode}: {result.Stderr.Trim()}");
    }

    /// <summary>Fast-forwards <paramref name="baseBranch"/> to <paramref name="branch"/> —
    /// the only merge shape chaining performs (SUG-16), and only once the human has
    /// confirmed "run next now?" after an Approve. False (no exception) when the base
    /// branch moved since the checkpoint branched, so a fast-forward is impossible —
    /// chaining must not proceed to the next checkpoint in that case (Prepare's own
    /// unmerged-base guard would refuse it anyway, with a worse message); the human
    /// resolves it manually. Never creates a merge commit or forces history. Leaves
    /// <paramref name="baseBranch"/> checked out on either outcome.</summary>
    public bool TryFastForwardMerge(string branch, string baseBranch)
    {
        if (!IsClean())
            throw new GitException(
                $"working tree at {RepoRoot} is not clean — refusing to merge over uncommitted changes");

        GitProcess.Run(RepoRoot, ["switch", baseBranch]);
        // Empirically verified (git 2.54.0): a genuine divergence exits 128 ("Not
        // possible to fast-forward, aborting"), not 1 — 1 is reserved for a bad ref
        // ("not something we can merge"), a caller error this method does not expect to
        // hit and should not swallow.
        var result = GitProcess.Run(RepoRoot, ["merge", "--ff-only", branch], allowNonZero: true);
        if (result.ExitCode is 0 or 128)
            return result.ExitCode == 0;
        throw new GitException(
            $"git merge --ff-only {branch} into {baseBranch} exited {result.ExitCode}: {result.Stderr.Trim()}");
    }

    /// <summary>Creates <paramref name="branch"/> at <paramref name="baseBranch"/> and
    /// switches to it. Refuses on a dirty tree — launching a checkpoint onto uncommitted
    /// work is exactly what §7.1 forbids — and refuses to reuse an existing branch
    /// implicitly; reruns go through <see cref="SwitchTo"/> as an explicit choice.</summary>
    public void CreateBranchFrom(string branch, string baseBranch)
    {
        if (!IsClean())
            throw new GitException(
                $"working tree at {RepoRoot} is not clean — commit or stash before launching a checkpoint (spec §7.1: never onto a dirty tree)");
        if (!BranchExists(baseBranch))
            throw new GitException($"base branch '{baseBranch}' does not exist in {RepoRoot}");
        if (BranchExists(branch))
            throw new GitException(
                $"branch '{branch}' already exists — switch to it explicitly for a re-attempt, or delete it first");

        GitProcess.Run(RepoRoot, ["switch", "-c", branch, baseBranch]);
    }

    public void SwitchTo(string branch)
    {
        if (!IsClean())
            throw new GitException(
                $"working tree at {RepoRoot} is not clean — refusing to switch branches over uncommitted changes");
        GitProcess.Run(RepoRoot, ["switch", branch]);
    }

    /// <summary>Stages everything and commits — the approval step made durable (spec
    /// §7.1: on human approval, Orca commits with a structured message). Returns the
    /// commit hash.</summary>
    public string StageAllAndCommit(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new GitException("commit message must be non-empty");
        if (IsClean())
            throw new GitException("nothing to commit — the checkpoint produced no changes");

        GitProcess.Run(RepoRoot, ["add", "-A"]);
        GitProcess.Run(RepoRoot, ["commit", "-m", message]);
        return GitProcess.Run(RepoRoot, ["rev-parse", "HEAD"]).Stdout.Trim();
    }

    /// <summary>Discards every uncommitted change, tracked (<c>reset --hard</c>) and
    /// untracked (<c>clean -fd</c>). Destructive by design and legal only as the
    /// confirmed-rejection path: the caller must have an explicit human confirmation in
    /// hand (spec §7.1 — offered, never automatic).</summary>
    public void DiscardAllChanges()
    {
        GitProcess.Run(RepoRoot, ["reset", "--hard", "HEAD"]);
        GitProcess.Run(RepoRoot, ["clean", "-fd"]);
    }

    // --- SUG-03 stage B: linked-worktree mechanics, so parallel checkpoints each get
    // their own tree while branches, objects, and refs stay shared with this repo. All
    // three operations run from RepoRoot; the worktree itself is opened as its own
    // GitWorkspace via Discover(path) afterwards.

    /// <summary>True when <paramref name="path"/> is a registered linked worktree of
    /// this repo (<c>git worktree list --porcelain</c>) — how a resumed worktree run
    /// recognizes its existing tree instead of failing to re-create it.</summary>
    public bool IsRegisteredWorktree(string path)
    {
        var full = Path.GetFullPath(path);
        return GitProcess.Run(RepoRoot, ["worktree", "list", "--porcelain"]).Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => line.StartsWith("worktree ", StringComparison.Ordinal)
                && Path.GetFullPath(line["worktree ".Length..]) == full);
    }

    /// <summary>Adds a detached worktree at <paramref name="commitish"/> — the fresh-run
    /// shape: the checkpoint branch does not exist yet, and the runner's own
    /// CreateBranchFrom creates it inside the worktree exactly as it would in the main
    /// tree (creating it here instead would trip Prepare's branch-without-evidence guard).</summary>
    public void AddWorktreeDetached(string path, string commitish)
    {
        GitProcess.Run(RepoRoot, ["worktree", "add", "--detach", path, commitish]);
    }

    /// <summary>Adds a worktree with the existing <paramref name="branch"/> checked out —
    /// the re-attempt shape. Git itself refuses when the branch is checked out anywhere
    /// else (including the main tree), which is the structural guard against two trees
    /// mutating one branch.</summary>
    public void AddWorktreeForBranch(string path, string branch)
    {
        GitProcess.Run(RepoRoot, ["worktree", "add", path, branch]);
    }

    /// <summary>Removes a linked worktree. Deliberately no <c>--force</c>: git refuses a
    /// dirty worktree, so undecided work can never be swept away by cleanup — the caller
    /// keeps the tree and tells the human where it lives instead.</summary>
    public void RemoveWorktree(string path)
    {
        GitProcess.Run(RepoRoot, ["worktree", "remove", path]);
    }
}
