using System.Text.Json;

namespace Orca.Core.Execution;

/// <summary>Generates the <c>--settings</c> JSON payload carrying safety layer 2.
/// Empirical basis (probed 2026-07-06 on 2.1.201, transcripts in the test fixtures):
/// under <c>--safe-mode</c>, <c>PreToolUse</c> deny hooks passed via <c>--settings</c>
/// never fire, but <c>permissions.deny</c> rules do — and they win over
/// <c>--allowedTools</c>. So the deny list rides this payload, written next to the run
/// log as evidence of what was in force, and hooks are not used at all.</summary>
public static class PermissionSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string ToJson(IReadOnlyList<string> deniedTools)
    {
        if (deniedTools.Count == 0)
            throw new ExecutorException("refusing to build a permissions payload with no deny rules — omit --settings instead");
        if (deniedTools.Any(string.IsNullOrWhiteSpace))
            throw new ExecutorException("deny patterns must be non-empty; got a blank entry");

        return JsonSerializer.Serialize(
            new { permissions = new { deny = deniedTools } }, Options) + "\n";
    }
}
