namespace Orca.Core.Git;

/// <summary>Deterministic git mechanics over the target repo (spec §7.1). Orca — never
/// the agent — creates checkpoint branches, commits on human approval, and discards on
/// confirmed rejection; the agent's allowlist carries no git write commands at all. Git
/// is the sandbox layer the whole design leans on hardest: nothing reaches the base
/// branch without a human reviewing the diff.</summary>
public sealed class GitWorkspace
{
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
}
