namespace Orca.Core.Execution;

/// <summary>Locates the <c>claude</c> CLI on PATH. Probes the bare name (native installs
/// symlink it, as on this project's machines) plus Windows npm-shim extensions
/// (<c>.exe</c>/<c>.cmd</c>/<c>.bat</c>), because npm installs ship a shim rather than a
/// real executable.</summary>
public static class ClaudeExecutableResolver
{
    public static string? Resolve()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        var candidates = OperatingSystem.IsWindows()
            ? new[] { "claude.exe", "claude.cmd", "claude.bat" }
            : new[] { "claude" };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                var full = Path.Combine(directory, candidate);
                if (File.Exists(full))
                    return full;
            }
        }

        return null;
    }
}
