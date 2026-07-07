using System.Globalization;
using System.Text;
using Orca.Core.Domain;

namespace Orca.Core.Vault;

/// <summary>Deterministic YAML emission for verification records — fixed key order, LF line
/// endings, always-quoted free-text strings — so records diff cleanly in git and round-trip
/// byte-stably through the parser. Timestamps are written in UTC.</summary>
public static class VerificationRecordSerializer
{
    public static string Serialize(VerificationRecord record)
    {
        StringBuilder sb = new();
        sb.Append("---\n");
        sb.Append("schema_version: ").Append(record.SchemaVersion).Append('\n');
        sb.Append("plan_id: ").Append(record.PlanId).Append('\n');
        sb.Append("plan_version: ").Append(record.PlanVersion).Append('\n');
        sb.Append("checkpoint_id: ").Append(record.CheckpointId).Append('\n');
        sb.Append("attempt: ").Append(record.Attempt).Append('\n');
        sb.Append("timestamp: ").Append(FormatTimestamp(record.Timestamp)).Append('\n');
        sb.Append("gates:\n");
        sb.Append("  build: ").Append(SchemaEnums.ToYaml(record.Gates.Build)).Append('\n');
        sb.Append("  tests: ").Append(SchemaEnums.ToYaml(record.Gates.Tests)).Append('\n');

        if (record.Semantic is null)
        {
            sb.Append("semantic: null\n");
        }
        else
        {
            sb.Append("semantic:\n");
            foreach (var item in record.Semantic)
            {
                sb.Append("  - criterion: ").Append(Quote(item.Criterion)).Append('\n');
                sb.Append("    verdict: ").Append(SchemaEnums.ToYaml(item.Verdict)).Append('\n');
                if (item.Note is not null)
                    sb.Append("    note: ").Append(Quote(item.Note)).Append('\n');
            }
        }

        sb.Append("verdict: ").Append(SchemaEnums.ToYaml(record.Verdict)).Append('\n');
        sb.Append("---\n");
        sb.Append(record.Body);
        return sb.ToString();
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
    {
        var format = timestamp.UtcTicks % TimeSpan.TicksPerSecond == 0
            ? "yyyy-MM-dd'T'HH:mm:ss'Z'"
            : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";
        return timestamp.UtcDateTime.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string Quote(string value) => YamlEmit.Quote(value);
}
