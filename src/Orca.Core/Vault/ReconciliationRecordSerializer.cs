using System.Text;
using Orca.Core.Domain;

namespace Orca.Core.Vault;

/// <summary>Deterministic YAML emission for reconciliation records — fixed key order, LF
/// line endings, always-quoted free-text strings — matching the verification-record
/// serializer so records diff cleanly in git and round-trip byte-stably.</summary>
public static class ReconciliationRecordSerializer
{
    public static string Serialize(ReconciliationRecord record)
    {
        StringBuilder sb = new();
        sb.Append("---\n");
        sb.Append("schema_version: ").Append(record.SchemaVersion).Append('\n');
        sb.Append("plan_id: ").Append(record.PlanId).Append('\n');
        sb.Append("plan_version: ").Append(record.PlanVersion).Append('\n');
        sb.Append("checkpoint_id: ").Append(record.CheckpointId).Append('\n');
        sb.Append("attempt: ").Append(record.Attempt).Append('\n');
        sb.Append("implements_plan: ").Append(SchemaEnums.ToYaml(record.ImplementsPlan)).Append('\n');
        AppendList(sb, "missing", record.Missing);
        AppendList(sb, "extra", record.Extra);
        sb.Append("---\n");
        sb.Append(record.Body);
        return sb.ToString();
    }

    private static void AppendList(StringBuilder sb, string key, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            sb.Append(key).Append(": []\n");
            return;
        }

        sb.Append(key).Append(":\n");
        foreach (var item in items)
            sb.Append("  - ").Append(YamlEmit.Quote(item)).Append('\n');
    }
}
