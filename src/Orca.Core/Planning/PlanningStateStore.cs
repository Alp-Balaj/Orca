using System.Text.Json;
using Orca.Core.Domain;
using Orca.Core.Vault;

namespace Orca.Core.Planning;

/// <summary>Persists <see cref="PlanningState"/> at
/// <c>.orca/state/&lt;plan-id&gt;.planning.json</c> in the target repo — beside the
/// checkpoint run state, never colliding with it (that file is
/// <c>&lt;plan-id&gt;.json</c>). Atomic temp-file + rename; overwriting is the point,
/// the attempt list inside is the append-only part.</summary>
public sealed class PlanningStateStore
{
    private readonly string _stateRoot;

    public PlanningStateStore(string targetRepoRoot)
    {
        if (string.IsNullOrWhiteSpace(targetRepoRoot))
            throw new ArgumentException("target repo root must be a non-empty path", nameof(targetRepoRoot));
        _stateRoot = Path.Combine(Path.GetFullPath(targetRepoRoot), ".orca", "state");
    }

    public string PathFor(string planId) =>
        Path.Combine(_stateRoot, VaultLayout.ValidatePlanId(planId) + ".planning.json");

    /// <summary>Null when no planning run has started; throws on malformed state — a
    /// half-parsed resume point is worse than none.</summary>
    public PlanningState? Read(string planId)
    {
        var path = PathFor(planId);
        if (!File.Exists(path))
            return null;
        try
        {
            return PlanningStateSerializer.FromJson(File.ReadAllText(path));
        }
        catch (JsonException e)
        {
            throw new JsonException($"{path}: {e.Message}", e);
        }
    }

    public string Write(PlanningState state)
    {
        var path = PathFor(state.PlanId);
        Directory.CreateDirectory(_stateRoot);

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, PlanningStateSerializer.ToJson(state));
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
