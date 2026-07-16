using System.Diagnostics;

namespace Orca.Tests;

/// <summary>Spawns the real built <c>Orca.Cli.dll</c> as a subprocess — via <c>dotnet</c>
/// rather than the platform-specific apphost, so the same helper runs on every CI OS. Used
/// to prove the process-boundary contract (stdout/stderr routing, exit codes, no leaked
/// stack traces) end to end, the way a shell invoking <c>orca</c> actually observes it —
/// something no in-process call into <c>CommandApp</c> can show.</summary>
internal static class CliProcess
{
    public static CliResult Run(string workingDirectory, params string[] args)
    {
        var cliDll = Path.Combine(AppContext.BaseDirectory, "Orca.Cli.dll");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(cliDll);
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new CliResult(process.ExitCode, stdout, stderr);
    }
}

internal sealed record CliResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>True if no line (trimmed) starts with a stack frame's "at " marker and
    /// neither stream mentions an unhandled exception — the signature of a refusal that
    /// escaped <c>ExitDecision</c> and crashed loud instead of exiting clean.</summary>
    public bool LooksLikeACrash =>
        StdOut.Contains("Unhandled exception", StringComparison.Ordinal) ||
        StdErr.Contains("Unhandled exception", StringComparison.Ordinal) ||
        HasStackFrameLine(StdOut) || HasStackFrameLine(StdErr);

    private static bool HasStackFrameLine(string text) =>
        text.Split('\n').Any(line => line.TrimStart().StartsWith("at ", StringComparison.Ordinal));
}
