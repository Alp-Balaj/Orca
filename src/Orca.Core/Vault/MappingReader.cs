using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Orca.Core.Vault;

/// <summary>Strict accessor over a YAML mapping: every read marks its key as consumed, and
/// <see cref="ThrowIfUnknownKeys"/> rejects whatever was never asked for. Unknown keys are
/// schema violations, not noise.</summary>
internal sealed class MappingReader
{
    /// <summary>Obsidian-native organizational keys tolerated (and ignored) so the owner
    /// can organize the vault without fighting the parser; everything else unknown is
    /// fatal.</summary>
    internal static readonly string[] ObsidianTolerated = ["tags", "aliases", "cssclasses"];

    private static readonly string[] NullSpellings = ["", "~", "null", "Null", "NULL"];

    private readonly Dictionary<string, YamlNode> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    private readonly string _filePath;
    private readonly string _context;

    internal MappingReader(YamlNode node, string filePath, string context)
    {
        _filePath = filePath;
        _context = context;
        if (node is not YamlMappingNode mapping)
            throw new VaultSchemaException(filePath, $"{context} must be a YAML mapping");

        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key })
                throw new VaultSchemaException(filePath, $"{context} contains a non-scalar key");
            if (!_entries.TryAdd(key, valueNode))
                throw new VaultSchemaException(filePath, $"{context} contains duplicate key '{key}'");
        }
    }

    internal bool Has(string key) => _entries.ContainsKey(key);

    internal string RequireString(string key)
    {
        var scalar = RequireScalar(key);
        if (IsNull(scalar) || string.IsNullOrWhiteSpace(scalar.Value))
            throw Violation($"'{key}' must be a non-empty string");
        return scalar.Value!;
    }

    internal string? OptionalString(string key) => Has(key) ? RequireString(key) : null;

    internal int RequireInt(string key)
    {
        var scalar = RequireScalar(key);
        if (IsNull(scalar) || !int.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw Violation($"'{key}' must be an integer");
        return value;
    }

    internal DateTimeOffset RequireTimestamp(string key)
    {
        var scalar = RequireScalar(key);
        if (!IsNull(scalar)
            && scalar.Value is { } raw
            && SchemaRegexes.Timestamp().IsMatch(raw)
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            return value;
        }

        throw Violation($"'{key}' must be an ISO-8601 timestamp with an explicit offset, e.g. 2026-07-06T14:00:00Z");
    }

    internal MappingReader RequireMapping(string key) =>
        new(RequireNode(key), _filePath, $"{_context}.{key}");

    internal IReadOnlyList<YamlNode> RequireSequence(string key)
    {
        if (RequireNode(key) is not YamlSequenceNode sequence)
            throw Violation($"'{key}' must be a list");
        return [.. sequence.Children];
    }

    internal IReadOnlyList<string> RequireStringList(string key)
    {
        var items = new List<string>();
        foreach (var node in RequireSequence(key))
        {
            if (node is not YamlScalarNode scalar || IsNull(scalar) || string.IsNullOrWhiteSpace(scalar.Value))
                throw Violation($"'{key}' items must be non-empty strings");
            items.Add(scalar.Value!);
        }

        if (items.Count == 0)
            throw Violation($"'{key}' must be a non-empty list");
        return items;
    }

    /// <summary>For keys whose value is either an explicit YAML null or a list
    /// (e.g. a verification record's <c>semantic</c>).</summary>
    internal IReadOnlyList<YamlNode>? RequireSequenceOrNull(string key)
    {
        var node = RequireNode(key);
        if (node is YamlScalarNode scalar && IsNull(scalar))
            return null;
        if (node is YamlSequenceNode sequence)
            return [.. sequence.Children];
        throw Violation($"'{key}' must be either null or a list");
    }

    internal void ThrowIfUnknownKeys(params string[] tolerated)
    {
        var unknown = _entries.Keys
            .Except(_consumed, StringComparer.Ordinal)
            .Except(tolerated, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0)
            throw Violation($"unknown key(s): {string.Join(", ", unknown.Select(k => $"'{k}'"))}");
    }

    private YamlNode RequireNode(string key)
    {
        if (!_entries.TryGetValue(key, out var node))
            throw Violation($"missing required key '{key}'");
        _consumed.Add(key);
        return node;
    }

    private YamlScalarNode RequireScalar(string key)
    {
        if (RequireNode(key) is not YamlScalarNode scalar)
            throw Violation($"'{key}' must be a scalar value");
        return scalar;
    }

    private static bool IsNull(YamlScalarNode scalar) =>
        scalar.Style == ScalarStyle.Plain && NullSpellings.Contains(scalar.Value ?? "");

    private VaultSchemaException Violation(string message) => new(_filePath, $"{_context}: {message}");
}
