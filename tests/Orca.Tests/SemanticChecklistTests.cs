using Orca.Core.Domain;
using Orca.Core.Vault;

namespace Orca.Tests;

/// <summary>The Verifier's §5.4 output contract: identity pinned to the request, criteria
/// 1:1 in order and byte-exact with the checkpoint's acceptance criteria, and a mandatory
/// note on every non-pass verdict — the note is the Reflexion feedback.</summary>
public sealed class SemanticChecklistTests
{
    private static readonly AttemptIdentity Identity = new("recipe-tagging", 1, "01-tag-entity", 1);
    private static readonly IReadOnlyList<string> Criteria = ["Tag entity exists", "EF mapping compiles"];

    private const string ValidText = """
        ---
        schema_version: 1
        plan_id: recipe-tagging
        plan_version: 1
        checkpoint_id: 01-tag-entity
        attempt: 1
        criteria:
          - criterion: "Tag entity exists"
            verdict: pass
          - criterion: "EF mapping compiles"
            verdict: fail
            note: "no DbSet or mapping configuration appears in the diff"
        ---
        The entity is there; the persistence side is not.
        """;

    private static SemanticChecklist Parse(string text) =>
        SemanticChecklistParser.ParseStaging(text, "01-tag-entity.attempt-1.semantic.md", Identity, Criteria);

    [Fact]
    public void Parses_the_documented_shape()
    {
        var checklist = Parse(ValidText);

        Assert.Equal(2, checklist.Criteria.Count);
        Assert.Equal(SemanticVerdict.Pass, checklist.Criteria[0].Verdict);
        Assert.Null(checklist.Criteria[0].Note);
        Assert.Equal(SemanticVerdict.Fail, checklist.Criteria[1].Verdict);
        Assert.Contains("DbSet", checklist.Criteria[1].Note);
        Assert.Contains("persistence side", checklist.Body);
    }

    [Fact]
    public void A_missing_criterion_is_rejected()
    {
        var text = ValidText.Replace("""
              - criterion: "EF mapping compiles"
                verdict: fail
                note: "no DbSet or mapping configuration appears in the diff"
            """, "");

        var ex = Assert.Throws<VaultSchemaException>(() => Parse(text));

        Assert.Contains("exactly one entry per acceptance criterion — expected 2, got 1", ex.Message);
    }

    [Fact]
    public void A_paraphrased_criterion_is_rejected()
    {
        var text = ValidText.Replace("\"Tag entity exists\"", "\"The Tag entity exists\"");

        var ex = Assert.Throws<VaultSchemaException>(() => Parse(text));

        Assert.Contains("byte-exact", ex.Message);
        Assert.Contains("no paraphrase", ex.Message);
    }

    [Fact]
    public void Reordered_criteria_are_rejected()
    {
        var text = """
            ---
            schema_version: 1
            plan_id: recipe-tagging
            plan_version: 1
            checkpoint_id: 01-tag-entity
            attempt: 1
            criteria:
              - criterion: "EF mapping compiles"
                verdict: pass
              - criterion: "Tag entity exists"
                verdict: pass
            ---
            """;

        var ex = Assert.Throws<VaultSchemaException>(() => Parse(text));

        Assert.Contains("criteria[0].criterion must be byte-exact", ex.Message);
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("unclear")]
    public void A_non_pass_verdict_without_a_note_is_rejected(string verdict)
    {
        var text = ValidText
            .Replace("verdict: fail\n    note: \"no DbSet or mapping configuration appears in the diff\"",
                $"verdict: {verdict}");

        var ex = Assert.Throws<VaultSchemaException>(() => Parse(text));

        Assert.Contains($"verdict '{verdict}' but no note", ex.Message);
    }

    [Fact]
    public void The_identity_must_match_the_request()
    {
        var ex = Assert.Throws<VaultSchemaException>(() =>
            SemanticChecklistParser.ParseStaging(
                ValidText, "x.md", Identity with { Attempt = 2 }, Criteria));

        Assert.Contains("'attempt' must be exactly '2'", ex.Message);
    }

    [Fact]
    public void Unknown_keys_are_rejected_at_both_levels()
    {
        var topLevel = ValidText.Replace("attempt: 1", "attempt: 1\nconfidence: high");
        Assert.Contains("unknown key",
            Assert.Throws<VaultSchemaException>(() => Parse(topLevel)).Message);

        var itemLevel = ValidText.Replace("    verdict: pass", "    verdict: pass\n    weight: 3");
        Assert.Contains("unknown key",
            Assert.Throws<VaultSchemaException>(() => Parse(itemLevel)).Message);
    }

    [Fact]
    public void An_unknown_verdict_is_rejected()
    {
        var text = ValidText.Replace("verdict: pass", "verdict: mostly-pass");

        var ex = Assert.Throws<VaultSchemaException>(() => Parse(text));

        Assert.Contains("expected pass|fail|unclear", ex.Message);
    }

    [Fact]
    public void An_explicit_null_note_reads_as_no_note_not_a_billed_retry()
    {
        // BF-11 §2 (probe-confirmed): 'note: null' / 'note:' is the natural spelling.
        var text = ValidText.Replace("verdict: pass", "verdict: pass\n    note: null");

        var checklist = Parse(text);

        Assert.Null(checklist.Criteria[0].Note);
    }

    [Fact]
    public void Byte_exact_mismatch_errors_visualize_trailing_whitespace()
    {
        // BF-11 §3 paper cut: 'ends with space ' vs 'ends with space' rendered
        // identically to a human.
        var text = ValidText.Replace("\"Tag entity exists\"", "\"Tag entity exists \"");

        var ex = Assert.Throws<VaultSchemaException>(() => Parse(text));

        Assert.Contains("\u2423", ex.Message);
    }
}
