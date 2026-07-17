using Orca.Core.Run;

namespace Orca.Tests;

/// <summary>SUG-03 stage B's safety prerequisite: the per-repo process lock.</summary>
public sealed class OrcaLockTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("orca-lock-").FullName;

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    [Fact]
    public void A_live_lock_refuses_a_second_acquire_naming_the_holder()
    {
        using var first = OrcaLock.Acquire(_repo);

        var ex = Assert.Throws<CheckpointRunException>(() => OrcaLock.Acquire(_repo));

        Assert.Contains($"pid {Environment.ProcessId}", ex.Message);
        Assert.Contains("delete", ex.Message);
    }

    [Fact]
    public void Dispose_releases_and_the_next_acquire_succeeds()
    {
        OrcaLock.Acquire(_repo).Dispose();

        Assert.False(File.Exists(OrcaLock.PathFor(_repo)));
        using var second = OrcaLock.Acquire(_repo);
    }

    [Fact]
    public void A_lock_from_a_dead_process_is_stale_and_silently_replaced()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".orca"));
        // PID 1 exists on Linux but is init — use an ID no test process will have.
        File.WriteAllText(OrcaLock.PathFor(_repo),
            $$"""{"Pid": 999999999, "AcquiredAt": "2026-07-17T00:00:00+00:00"}""");

        using var acquired = OrcaLock.Acquire(_repo);

        Assert.NotNull(acquired);
    }

    [Fact]
    public void An_unreadable_lock_file_counts_as_stale_never_a_crash()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".orca"));
        File.WriteAllText(OrcaLock.PathFor(_repo), "not json");

        using var acquired = OrcaLock.Acquire(_repo);

        Assert.NotNull(acquired);
    }
}
