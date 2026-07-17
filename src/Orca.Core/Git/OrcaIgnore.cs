namespace Orca.Core.Git;

/// <summary>Ensures <c>.orca/</c> is gitignored in the target repo (spec §3: run logs and
/// state are never committed). Load-bearing for two mechanics: approval's
/// <c>git add -A</c> must never sweep run logs into the target repo's history, and
/// rejection's <c>git clean -fd</c> must never delete them (ignored files survive
/// <c>-fd</c>).</summary>
public static class OrcaIgnore
{
    public const string Entry = ".orca/";

    /// <summary>True when the entry had to be appended — the caller must surface that the
    /// change needs committing before a checkpoint can launch (Orca never commits to the
    /// base branch on its own).</summary>
    public static bool EnsureIgnored(string repoRoot)
    {
        // BF-17: ask git itself (check-ignore), not the root .gitignore's text — a
        // global ignore or info/exclude made the textual check append pointlessly, and
        // a later `!.orca/` negation passed the text check while git did NOT ignore,
        // which would have committed run logs on the next approve. Appending after a
        // negation works because the last matching gitignore rule wins.
        var check = GitProcess.Run(repoRoot, ["check-ignore", "-q", "--", ".orca/"], allowNonZero: true);
        if (check.ExitCode == 0)
            return false;

        var path = Path.Combine(repoRoot, ".gitignore");
        if (check.ExitCode != 1 && File.Exists(path))
        {
            // git couldn't answer (not a work tree — some tests, odd states): the old
            // textual check is the honest fallback.
            var ignored = File.ReadAllLines(path)
                .Select(line => line.Trim())
                .Any(line => line is ".orca" or ".orca/" or "/.orca" or "/.orca/");
            if (ignored)
                return false;
        }

        using var writer = File.AppendText(path);
        writer.NewLine = "\n";
        writer.WriteLine();
        writer.WriteLine("# Orca run logs and state — ephemeral evidence, never committed (spec §5.3)");
        writer.WriteLine(Entry);
        return true;
    }
}
