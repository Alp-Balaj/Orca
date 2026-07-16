using Orca.Core.Domain;
using Orca.Core.Vault;

namespace Orca.Tests;

/// <summary>The reconciliation-record contract (step 7, owner-approved 2026-07-07):
/// consistency rules, deterministic byte-stable serialization, and strict parsing
/// through both entry points — canonical (file name must match) and staging (identity
/// must match the request).</summary>
public sealed class ReconciliationRecordTests
{
    private static readonly AttemptIdentity Identity = new("recipe-tagging", 1, "01-tag-entity", 2);

    private static ReconciliationRecord Record(
        ReconciliationVerdict verdict = ReconciliationVerdict.Full,
        IReadOnlyList<string>? missing = null,
        IReadOnlyList<string>? extra = null) => new(
        1, "recipe-tagging", 1, "01-tag-entity", 2,
        verdict, missing ?? [], extra ?? [], "Reasoning.\n");

    private const string ValidText = """
        ---
        schema_version: 1
        plan_id: recipe-tagging
        plan_version: 1
        checkpoint_id: 01-tag-entity
        attempt: 2
        implements_plan: partial
        missing:
          - "PUT /tags/{id} endpoint from the plan section is absent"
        extra:
          - "adds an unrequested TagColor column"
        ---
        The diff covers the entity but not the update endpoint.
        """;

    [Fact]
    public void A_valid_record_round_trips_byte_stably()
    {
        var record = Record(ReconciliationVerdict.Partial, missing: ["PUT endpoint absent"], extra: ["extra column"]);
        var text = ReconciliationRecordSerializer.Serialize(record);
        var reparsed = ReconciliationRecordParser.Parse(text, "01-tag-entity.attempt-2.md");

        Assert.Equal(text, ReconciliationRecordSerializer.Serialize(reparsed));
        Assert.Equal(ReconciliationVerdict.Partial, reparsed.ImplementsPlan);
        Assert.Equal(["PUT endpoint absent"], reparsed.Missing);
        Assert.Equal(["extra column"], reparsed.Extra);
        Assert.Equal("Reasoning.\n", reparsed.Body);
    }

    [Fact]
    public void Empty_lists_serialize_as_flow_sequences_and_parse_back()
    {
        var text = ReconciliationRecordSerializer.Serialize(Record());

        Assert.Contains("missing: []", text);
        Assert.Contains("extra: []", text);
        var reparsed = ReconciliationRecordParser.Parse(text, "01-tag-entity.attempt-2.md");
        Assert.Empty(reparsed.Missing);
        Assert.Empty(reparsed.Extra);
    }

    [Fact]
    public void Missing_and_extra_items_round_trip_every_line_separator()
    {
        var separators = new string([(char)0x0085, (char)0x2028, (char)0x2029]);
        var missingItem = $"missing{separators}item";
        var extraItem = $"extra{separators}item";

        var record = Record(ReconciliationVerdict.Partial, missing: [missingItem], extra: [extraItem]);
        var text = ReconciliationRecordSerializer.Serialize(record);
        var reparsed = ReconciliationRecordParser.Parse(text, "01-tag-entity.attempt-2.md");

        Assert.Equal(text, ReconciliationRecordSerializer.Serialize(reparsed));
        Assert.Equal([missingItem], reparsed.Missing);
        Assert.Equal([extraItem], reparsed.Extra);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x1b)]
    [InlineData(0x1f)]
    [InlineData(0x80)]
    [InlineData(0x9f)]
    public void Other_c0_and_c1_controls_round_trip_byte_stably_in_an_extra_item(int codePoint)
    {
        var extraItem = $"extra{(char)codePoint}item";

        var record = Record(extra: [extraItem]);
        var text = ReconciliationRecordSerializer.Serialize(record);
        var reparsed = ReconciliationRecordParser.Parse(text, "01-tag-entity.attempt-2.md");

        Assert.Equal(text, ReconciliationRecordSerializer.Serialize(reparsed));
        Assert.Equal([extraItem], reparsed.Extra);
    }

    [Fact]
    public void Parses_the_documented_shape()
    {
        var record = ReconciliationRecordParser.Parse(ValidText, "01-tag-entity.attempt-2.md");

        Assert.Equal(ReconciliationVerdict.Partial, record.ImplementsPlan);
        Assert.Single(record.Missing);
        Assert.Single(record.Extra);
        Assert.Contains("update endpoint", record.Body);
    }

    [Fact]
    public void Full_with_missing_items_is_inconsistent()
    {
        var ex = Assert.Throws<VaultSchemaException>(() =>
            ReconciliationRules.Validate(Record(missing: ["something"]), "ctx"));

        Assert.Contains("inconsistent with a non-empty missing list", ex.Message);
    }

    [Theory]
    [InlineData(ReconciliationVerdict.Partial)]
    [InlineData(ReconciliationVerdict.None)]
    public void A_nonconformance_claim_must_say_what_is_missing(ReconciliationVerdict verdict)
    {
        var ex = Assert.Throws<VaultSchemaException>(() =>
            ReconciliationRules.Validate(Record(verdict), "ctx"));

        Assert.Contains("requires a non-empty missing list", ex.Message);
    }

    [Fact]
    public void Blank_list_items_are_rejected()
    {
        var ex = Assert.Throws<VaultSchemaException>(() =>
            ReconciliationRules.Validate(Record(extra: [" "]), "ctx"));

        Assert.Contains("'extra' items must be non-empty strings", ex.Message);
    }

    [Fact]
    public void An_unknown_verdict_is_rejected()
    {
        var text = ValidText.Replace("implements_plan: partial", "implements_plan: mostly");

        var ex = Assert.Throws<VaultSchemaException>(() =>
            ReconciliationRecordParser.Parse(text, "01-tag-entity.attempt-2.md"));

        Assert.Contains("expected full|partial|none", ex.Message);
    }

    [Fact]
    public void Unknown_keys_are_rejected()
    {
        var text = ValidText.Replace("attempt: 2", "attempt: 2\nseverity: high");

        var ex = Assert.Throws<VaultSchemaException>(() =>
            ReconciliationRecordParser.Parse(text, "01-tag-entity.attempt-2.md"));

        Assert.Contains("unknown key", ex.Message);
    }

    [Fact]
    public void The_canonical_file_name_must_match_the_frontmatter()
    {
        var ex = Assert.Throws<VaultSchemaException>(() =>
            ReconciliationRecordParser.Parse(ValidText, "01-tag-entity.attempt-3.md"));

        Assert.Contains("file name must be '01-tag-entity.attempt-2.md'", ex.Message);
    }

    [Fact]
    public void Staging_parse_ignores_the_file_name_but_pins_the_identity()
    {
        // The staging file name is Orca-chosen and never matches the canonical pattern.
        var record = ReconciliationRecordParser.ParseStaging(
            ValidText, "01-tag-entity.attempt-2.reconciliation.md", Identity);
        Assert.Equal(2, record.Attempt);

        var wrongAttempt = Identity with { Attempt = 3 };
        var ex = Assert.Throws<VaultSchemaException>(() =>
            ReconciliationRecordParser.ParseStaging(
                ValidText, "01-tag-entity.attempt-2.reconciliation.md", wrongAttempt));
        Assert.Contains("'attempt' must be exactly '3'", ex.Message);
    }

    [Fact]
    public void Staging_parse_rejects_an_answer_for_a_different_plan()
    {
        var foreign = Identity with { PlanId = "other-plan" };

        var ex = Assert.Throws<VaultSchemaException>(() =>
            ReconciliationRecordParser.ParseStaging(ValidText, "x.md", foreign));

        Assert.Contains("'plan_id' must be exactly 'other-plan'", ex.Message);
    }

    [Fact]
    public void The_writer_validates_and_refuses_to_overwrite()
    {
        var vault = Directory.CreateTempSubdirectory("orca-reconciliation-vault-").FullName;
        try
        {
            var writer = new VaultWriter(new VaultLayout(vault, "Plans"));
            var record = Record(ReconciliationVerdict.Partial, missing: ["the endpoint"]);

            var path = writer.WriteReconciliationRecord(record);
            Assert.EndsWith("01-tag-entity.attempt-2.md", path);

            // Append-only: the same attempt can never be re-written.
            var ex = Assert.Throws<VaultException>(() => writer.WriteReconciliationRecord(record));
            Assert.Contains("append-only", ex.Message);

            // And an inconsistent record never reaches disk.
            Assert.Throws<VaultSchemaException>(() =>
                writer.WriteReconciliationRecord(Record(missing: ["x"]) with { Attempt = 3 }));
        }
        finally
        {
            Directory.Delete(vault, recursive: true);
        }
    }

    [Fact]
    public void Filename_mismatch_error_names_the_sync_tool_trap()
    {
        var ex = Assert.Throws<VaultSchemaException>(() =>
            ReconciliationRecordParser.Parse(ValidText, "01-tag-entity.attempt-2.sync-conflict-20260716.md"));

        Assert.Contains("sync tool", ex.Message);
    }

    [Fact]
    public void Staging_parse_accepts_CRLF_output_from_a_windows_machine()
    {
        // BF-09 §5: committed fixtures are LF-normalized, so the shape a Windows-machine
        // Claude actually writes had no test keeping it working.
        var record = ReconciliationRecordParser.ParseStaging(
            ValidText.ReplaceLineEndings("\r\n"), "01-tag-entity.attempt-2.reconciliation.md", Identity);

        Assert.Equal(2, record.Attempt);
    }
}
