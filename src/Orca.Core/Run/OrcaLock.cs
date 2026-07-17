using System.Diagnostics;
using System.Text.Json;

namespace Orca.Core.Run;

/// <summary>SUG-03 stage B's safety prerequisite: a per-repo <c>.orca/lock</c> so two
/// Orca processes (TUI + CLI, or two terminals) can't double-run one repo even
/// accidentally — before this, nothing prevented two sessions from fighting over the
/// same working tree, run state, and branch. PID + timestamp on disk; a lock whose
/// process is gone (or that has sat for over an hour after a crash that skipped
/// disposal) is stale and silently replaced. Dispose releases.
/// <para>Stage B's worktree runs narrow the unit: a <paramref name="scope"/>-qualified
/// lock (<c>.orca/lock-&lt;plan-id&gt;</c>) guards one plan's run state and records, so
/// two plans can run in parallel worktrees while two runs of the SAME plan — which would
/// fight over <c>.orca/state/&lt;plan&gt;.json</c> and attempt numbering — still refuse.
/// Main-tree checkpoint runs take the repo lock AND their plan's lock; worktree runs
/// take only the plan lock (their tree is private, the plan state is not).</para></summary>
public sealed class OrcaLock : IDisposable
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    private readonly string _path;
    private bool _released;

    private OrcaLock(string path) => _path = path;

    public static string PathFor(string repoRoot, string? scope = null) =>
        Path.Combine(repoRoot, ".orca", scope is null ? "lock" : $"lock-{ValidateScope(scope)}");

    /// <summary>Takes the lock or throws with the holder named. Callers wrap mutating
    /// runs (checkpoint-run, plan-generate, goal) — read-only commands never lock.</summary>
    public static OrcaLock Acquire(string repoRoot, TimeProvider? clock = null, string? scope = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var path = PathFor(repoRoot, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var unit = scope is null ? "this repo" : $"plan '{scope}' in this repo";
        if (ReadHolder(path) is { } holder)
        {
            var stale = !ProcessAlive(holder.Pid) || now - holder.AcquiredAt > StaleAfter;
            if (!stale)
                throw new CheckpointRunException(
                    $"another Orca process (pid {holder.Pid}, since {holder.AcquiredAt:HH:mm:ss} UTC) is already " +
                    $"running against {unit} — two sessions would fight over one working tree and run state. " +
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

    /// <summary>The scope becomes a file-name suffix, so it must stay inside the plan-id
    /// alphabet — the same shape <see cref="Orca.Core.Vault.VaultLayout"/> enforces.</summary>
    private static string ValidateScope(string scope) =>
        scope.Length > 0 && scope.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_')
            ? scope
            : throw new ArgumentException($"lock scope '{scope}' is not a valid plan id", nameof(scope));

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
