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
}
