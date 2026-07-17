using System.Diagnostics;
using System.Text.Json;

namespace Orca.Core.Run;

/// <summary>SUG-03 stage B's safety prerequisite: a per-repo <c>.orca/lock</c> so two
/// Orca processes (TUI + CLI, or two terminals) can't double-run one repo even
/// accidentally — before this, nothing prevented two sessions from fighting over the
/// same working tree, run state, and branch. PID + timestamp on disk; a lock whose
/// process is gone (or that has sat for over an hour after a crash that skipped
/// disposal) is stale and silently replaced. Dispose releases.</summary>
public sealed class OrcaLock : IDisposable
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    private readonly string _path;
    private bool _released;

    private OrcaLock(string path) => _path = path;

    public static string PathFor(string repoRoot) => Path.Combine(repoRoot, ".orca", "lock");

    /// <summary>Takes the lock or throws with the holder named. Callers wrap mutating
    /// runs (checkpoint-run, plan-generate, goal) — read-only commands never lock.</summary>
    public static OrcaLock Acquire(string repoRoot, TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var path = PathFor(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (ReadHolder(path) is { } holder)
        {
            var stale = !ProcessAlive(holder.Pid) || now - holder.AcquiredAt > StaleAfter;
            if (!stale)
                throw new CheckpointRunException(
                    $"another Orca process (pid {holder.Pid}, since {holder.AcquiredAt:HH:mm:ss} UTC) is already " +
                    $"running against this repo — two sessions would fight over one working tree and run state. " +
                    $"Finish or stop it first; if it crashed, delete {path}.");
        }

        File.WriteAllText(path, JsonSerializer.Serialize(new LockHolder(
            Environment.ProcessId, now)));
        return new OrcaLock(path);
    }

    public void Dispose()
    {
        if (_released)
            return;
        _released = true;
        try
        {
            // Only the holder deletes its own lock — a newer process may have replaced
            // a stale file this instance once wrote.
            if (ReadHolder(_path)?.Pid == Environment.ProcessId)
                File.Delete(_path);
        }
        catch (IOException)
        {
            // A stuck lock file degrades to the staleness path, never to a crash.
        }
    }

    private sealed record LockHolder(int Pid, DateTimeOffset AcquiredAt);

    private static LockHolder? ReadHolder(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<LockHolder>(File.ReadAllText(path));
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null; // unreadable = stale
        }
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            return !Process.GetProcessById(pid).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
