using System.Text.Json;
using Orca.Core.Domain;
using Orca.Core.Vault;

namespace Orca.Core.Run;

/// <summary>Persists <see cref="RunState"/> at <c>.orca/state/&lt;plan-id&gt;.json</c> in
/// the target repo (gitignored, like the run logs — resumability state, not durable
/// knowledge). Writes are atomic temp-file + rename; unlike verification records, state is
/// mutable and overwriting is the point.</summary>
public sealed class RunStateStore
{
    private readonly string _stateRoot;

    public RunStateStore(string targetRepoRoot)
    {
        if (string.IsNullOrWhiteSpace(targetRepoRoot))
            throw new ArgumentException("target repo root must be a non-empty path", nameof(targetRepoRoot));
        _stateRoot = Path.Combine(Path.GetFullPath(targetRepoRoot), ".orca", "state");
    }

    public string PathFor(string planId) =>
        Path.Combine(_stateRoot, VaultLayout.ValidatePlanId(planId) + ".json");

    /// <summary>Removes the state file (BF-12 §3: rolling back a pre-spawn failure on a
    /// first attempt, where there is no previous state to restore). Missing is fine.</summary>
    public void Delete(string planId)
    {
        var path = PathFor(planId);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Null when no run has started; throws on malformed state — a half-parsed
    /// resume point is worse than none.</summary>
    public RunState? Read(string planId)
    {
        var path = PathFor(planId);
        if (!File.Exists(path))
            return null;
        try
        {
            return RunStateSerializer.FromJson(File.ReadAllText(path));
        }
        catch (JsonException e)
        {
            throw new JsonException($"{path}: {e.Message}", e);
        }
    }

    public string Write(RunState state)
    {
        var path = PathFor(state.PlanId);
        Directory.CreateDirectory(_stateRoot);

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, RunStateSerializer.ToJson(state));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }

        return path;
    }
}
