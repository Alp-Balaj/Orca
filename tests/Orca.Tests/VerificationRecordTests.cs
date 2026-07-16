using Orca.Core.Domain;
using Orca.Core.Vault;

namespace Orca.Tests;

public class VerificationRecordTests
{
    private const string FailedGatesRecord = """
        ---
        schema_version: 1
        plan_id: recipe-tagging
        plan_version: 1
        checkpoint_id: 01-tag-entity
        attempt: 1
        timestamp: 2026-07-06T15:20:00Z
        gates:
          build: pass
          tests: fail
        semantic: null
        verdict: fail
        ---
        Failing tests: TagRepositoryTests.Unique_index_is_enforced.
        """;

    private const string PassingRecord = """
        ---
        schema_version: 1
        plan_id: recipe-tagging
        plan_version: 1
        checkpoint_id: 01-tag-entity
        attempt: 2
        timestamp: 2026-07-06T16:05:00Z
        gates:
          build: pass
          tests: pass
        semantic:
          - criterion: "Tag entity with unique (UserId, Name)"
            verdict: pass
            note: "unique index present in the migration"
          - criterion: "migration added but never applied"
            verdict: pass
        verdict: pass
        ---
        All criteria satisfied on the second attempt.
        """;

    [Fact]
    public void Parses_a_record_where_a_deterministic_gate_failed()
    {
        var record = VerificationRecordParser.Parse(FailedGatesRecord, "01-tag-entity.attempt-1.md");

        Assert.Equal(GateOutcome.Pass, record.Gates.Build);
        Assert.Equal(GateOutcome.Fail, record.Gates.Tests);
        Assert.Null(record.Semantic);
        Assert.Equal(VerificationVerdict.Fail, record.Verdict);
        Assert.Contains("Failing tests", record.Body);
    }

    [Fact]
    public void A_record_with_no_working_tree_hash_key_parses_it_as_null()
    {
        // FailedGatesRecord/PassingRecord predate BF-19 and never write the key —
        // exactly what an old, already-in-the-vault record looks like.
        Assert.Null(VerificationRecordParser.Parse(FailedGatesRecord, "01-tag-entity.attempt-1.md").WorkingTreeHash);
        Assert.Null(VerificationRecordParser.Parse(PassingRecord, "01-tag-entity.attempt-2.md").WorkingTreeHash);
    }

    [Fact]
    public void Working_tree_hash_round_trips_when_present()
    {
        var text = PassingRecord.Replace(
            "  tests: pass\n", "  tests: pass\nworking_tree_hash: 74b85cf3a04f6222215ca0af424b55aa2ebf0c75\n");

        var record = VerificationRecordParser.Parse(text, "01-tag-entity.attempt-2.md");
        Assert.Equal("74b85cf3a04f6222215ca0af424b55aa2ebf0c75", record.WorkingTreeHash);

        var serialized = VerificationRecordSerializer.Serialize(record);
        Assert.Contains("working_tree_hash: 74b85cf3a04f6222215ca0af424b55aa2ebf0c75\n", serialized);
        Assert.Equal(record.WorkingTreeHash, VerificationRecordParser.Parse(serialized, "01-tag-entity.attempt-2.md").WorkingTreeHash);
    }

    [Fact]
    public void Serializer_omits_the_key_entirely_when_the_hash_is_absent()
    {
        var record = VerificationRecordParser.Parse(PassingRecord, "01-tag-entity.attempt-2.md");
        Assert.Null(record.WorkingTreeHash);

        Assert.DoesNotContain("working_tree_hash", VerificationRecordSerializer.Serialize(record));
    }

    [Fact]
    public void Rules_reject_an_empty_working_tree_hash()
    {
        var record = new VerificationRecord(
            1, "recipe-tagging", 1, "01-tag-entity", 1,
            DateTimeOffset.Parse("2026-07-06T15:20:00Z"),
            new GateResults(GateOutcome.Pass, GateOutcome.Pass),
            [new SemanticCriterionResult("criterion", SemanticVerdict.Pass, null)],
            VerificationVerdict.Pass, "Body.\n", WorkingTreeHash: "   ");

        var ex = Assert.Throws<VaultSchemaException>(() => VerificationRules.Validate(record, "context"));
        Assert.Contains("working_tree_hash", ex.Message);
    }

    [Fact]
    public void Parses_a_passing_record_with_semantic_results()
    {
        var record = VerificationRecordParser.Parse(PassingRecord, "01-tag-entity.attempt-2.md");

        Assert.Equal(2, record.Attempt);
        Assert.NotNull(record.Semantic);
        Assert.Equal(2, record.Semantic.Count);
        Assert.Equal(SemanticVerdict.Pass, record.Semantic[0].Verdict);
        Assert.Equal("unique index present in the migration", record.Semantic[0].Note);
        Assert.Null(record.Semantic[1].Note);
        Assert.Equal(VerificationVerdict.Pass, record.Verdict);
    }

    [Fact]
    public void Serializer_round_trips_byte_stably()
    {
        var first = VerificationRecordParser.Parse(PassingRecord, "01-tag-entity.attempt-2.md");
        var serialized = VerificationRecordSerializer.Serialize(first);
        var second = VerificationRecordParser.Parse(serialized, "01-tag-entity.attempt-2.md");

        Assert.Equal(serialized, VerificationRecordSerializer.Serialize(second));
        Assert.Equal(first.Timestamp, second.Timestamp);
        Assert.Equal(first.Body, second.Body);
    }

    [Fact]
    public void An_unclear_criterion_with_an_overall_fail_verdict_is_valid()
    {
        var text = PassingRecord
            .Replace("    verdict: pass\n    note:", "    verdict: unclear\n    note:")
            .Replace("\nverdict: pass", "\nverdict: fail");

        var record = VerificationRecordParser.Parse(text, "01-tag-entity.attempt-2.md");

        Assert.Equal(SemanticVerdict.Unclear, record.Semantic![0].Verdict);
        Assert.Equal(VerificationVerdict.Fail, record.Verdict);
    }

    [Fact]
    public void Rejects_semantic_results_when_a_gate_failed()
    {
        var text = PassingRecord.Replace("  tests: pass", "  tests: fail");

        var ex = Assert.Throws<VaultSchemaException>(
            () => VerificationRecordParser.Parse(text, "01-tag-entity.attempt-2.md"));

        Assert.Contains("semantic must be null", ex.Message);
    }

    [Fact]
    public void Rejects_a_missing_semantic_layer_when_both_gates_passed()
    {
        var text = FailedGatesRecord.Replace("  tests: fail", "  tests: pass");

        var ex = Assert.Throws<VaultSchemaException>(
            () => VerificationRecordParser.Parse(text, "01-tag-entity.attempt-1.md"));

        Assert.Contains("semantic results are required", ex.Message);
    }

    [Fact]
    public void Rejects_a_verdict_inconsistent_with_the_criteria()
    {
        var text = PassingRecord.Replace(
            "    verdict: pass\n    note: \"unique index present in the migration\"",
            "    verdict: fail\n    note: \"unique index present in the migration\"");

        var ex = Assert.Throws<VaultSchemaException>(
            () => VerificationRecordParser.Parse(text, "01-tag-entity.attempt-2.md"));

        Assert.Contains("inconsistent", ex.Message);
    }

    [Fact]
    public void Rejects_a_file_name_that_disagrees_with_the_frontmatter()
    {
        var ex = Assert.Throws<VaultSchemaException>(
            () => VerificationRecordParser.Parse(PassingRecord, "01-tag-entity.attempt-3.md"));

        Assert.Contains("file name must be '01-tag-entity.attempt-2.md'", ex.Message);
    }

    [Fact]
    public void Rejects_an_attempt_number_below_one()
    {
        var text = FailedGatesRecord.Replace("attempt: 1", "attempt: 0");

        var ex = Assert.Throws<VaultSchemaException>(
            () => VerificationRecordParser.Parse(text, "01-tag-entity.attempt-0.md"));

        Assert.Contains("attempt must be >= 1", ex.Message);
    }

    [Fact]
    public void Semantic_note_and_criterion_round_trip_every_line_separator()
    {
        var separators = new string([(char)0x0085, (char)0x2028, (char)0x2029]);
        var criterion = $"criterion{separators}with separators";
        var note = $"note{separators}with separators";

        var record = new VerificationRecord(
            1, "recipe-tagging", 1, "01-tag-entity", 1,
            DateTimeOffset.Parse("2026-07-06T15:20:00Z"),
            new GateResults(GateOutcome.Pass, GateOutcome.Pass),
            [new SemanticCriterionResult(criterion, SemanticVerdict.Pass, note)],
            VerificationVerdict.Pass, "Body.\n");

        var serialized = VerificationRecordSerializer.Serialize(record);
        var parsed = VerificationRecordParser.Parse(serialized, "01-tag-entity.attempt-1.md");

        Assert.Equal(criterion, parsed.Semantic![0].Criterion);
        Assert.Equal(note, parsed.Semantic[0].Note);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x1b)]
    [InlineData(0x1f)]
    [InlineData(0x80)]
    [InlineData(0x9f)]
    public void Other_c0_and_c1_controls_round_trip_in_a_semantic_note(int codePoint)
    {
        var note = $"note{(char)codePoint}with-control";

        var record = new VerificationRecord(
            1, "recipe-tagging", 1, "01-tag-entity", 1,
            DateTimeOffset.Parse("2026-07-06T15:20:00Z"),
            new GateResults(GateOutcome.Pass, GateOutcome.Pass),
            [new SemanticCriterionResult("criterion", SemanticVerdict.Pass, note)],
            VerificationVerdict.Pass, "Body.\n");

        var serialized = VerificationRecordSerializer.Serialize(record);
        var parsed = VerificationRecordParser.Parse(serialized, "01-tag-entity.attempt-1.md");

        Assert.Equal(note, parsed.Semantic![0].Note);
    }
}
