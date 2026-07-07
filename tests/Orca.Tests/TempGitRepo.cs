using System.Diagnostics;

namespace Orca.Tests;

/// <summary>A throwaway git repo with a deterministic in-repo identity, one initial
/// commit on <c>main</c>, and recursive cleanup. Requires <c>git</c> on PATH — a given on
/// the development machines.</summary>
internal sealed class TempGitRepo : IDisposable
{
    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "orca-git-tests-" + Guid.NewGuid().ToString("N"));

    public TempGitRepo()
    {
        Directory.CreateDirectory(Root);
        Git("init", "-b", "main");
        Git("config", "user.name", "Orca Tests");
        Git("config", "user.email", "tests@orca.local");
        // Byte-identical checkouts on both development machines: a Windows-default
        // autocrlf=true would rewrite LF fixtures as CRLF on reset/checkout.
        Git("config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(Root, "README.md"), "seed\n");
        Git("add", "-A");
        Git("commit", "-m", "seed");
    }

    public string Git(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }

    public void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
