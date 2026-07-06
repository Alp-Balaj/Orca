namespace Orca.Core.Git;

/// <summary>A git operation failed or the repository is in a state Orca refuses to work
/// from (dirty tree, detached HEAD, missing base branch). Carries git's own stderr where
/// there is one — the CLI's message beats a paraphrase.</summary>
public class GitException : Exception
{
    public GitException(string message)
        : base(message)
    {
    }

    public GitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
