using System.ComponentModel;
using System.Diagnostics;

namespace Orca.Core.Git;

/// <summary>Runs one <c>git</c> command as a plain process call — argv via
/// <c>ArgumentList</c>, no shell, same philosophy as the deterministic gates (spec §8:
/// never ask a model for a fact the machine can state; here, never wrap git in anything
/// smarter than a process spawn).</summary>
internal static class GitProcess
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    internal sealed record Result(int ExitCode, string Stdout, string Stderr);

    /// <summary>Runs git and throws on a nonzero exit unless <paramref name="allowNonZero"/>;
    /// callers that branch on exit codes (e.g. ref existence checks) opt in.
    /// <paramref name="environment"/> overrides/adds on top of the inherited environment
    /// (e.g. <c>GIT_INDEX_FILE</c> to operate on a scratch index without touching the
    /// repo's real one) — never a replacement, so PATH etc. still resolve normally.</summary>
    internal static Result Run(
        string workingDirectory, IReadOnlyList<string> arguments, bool allowNonZero = false,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new GitException("failed to start git process");
        }
        catch (Win32Exception e)
        {
            throw new GitException("git not found on PATH — install git before running Orca", e);
        }

        using (process)
        {
            process.StandardInput.Close();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new GitException(
                    $"git {string.Join(' ', arguments)} did not finish within {Timeout.TotalSeconds:0}s and was killed");
            }

            var result = new Result(process.ExitCode, stdoutTask.Result, stderrTask.Result);
            if (result.ExitCode != 0 && !allowNonZero)
                throw new GitException(
                    $"git {string.Join(' ', arguments)} exited {result.ExitCode}: {result.Stderr.Trim()}");
            return result;
        }
    }
}
