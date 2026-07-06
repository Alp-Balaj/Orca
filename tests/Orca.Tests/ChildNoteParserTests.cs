using Orca.Core.Vault;

namespace Orca.Tests;

public class ChildNoteParserTests
{
    private const string ValidNote = """
        ---
        schema_version: 1
        plan_id: recipe-tagging
        checkpoint_id: 01-tag-entity
        ---
        Implement the Tag entity exactly as described in the plan.
        """;

    [Fact]
    public void Parses_a_valid_child_note()
    {
        var note = ChildNoteParser.Parse(ValidNote, "01-tag-entity.md");

        Assert.Equal(1, note.SchemaVersion);
        Assert.Equal("recipe-tagging", note.PlanId);
        Assert.Equal("01-tag-entity", note.CheckpointId);
        Assert.Contains("Implement the Tag entity", note.Body);
    }

    [Fact]
    public void Tolerates_obsidian_organizational_keys()
    {
        var text = ValidNote.Replace("checkpoint_id: 01-tag-entity", "checkpoint_id: 01-tag-entity\ntags:\n  - orca");

        var note = ChildNoteParser.Parse(text, "01-tag-entity.md");

        Assert.Equal("01-tag-entity", note.CheckpointId);
    }

    [Fact]
    public void Rejects_a_file_name_that_does_not_match_the_checkpoint_id()
    {
        var ex = Assert.Throws<VaultSchemaException>(() => ChildNoteParser.Parse(ValidNote, "02-other.md"));

        Assert.Contains("must equal the file name stem", ex.Message);
    }

    [Fact]
    public void Rejects_unknown_keys()
    {
        var text = ValidNote.Replace("plan_id: recipe-tagging", "plan_id: recipe-tagging\nauthor: alp");

        var ex = Assert.Throws<VaultSchemaException>(() => ChildNoteParser.Parse(text, "01-tag-entity.md"));

        Assert.Contains("unknown key", ex.Message);
    }

    [Fact]
    public void Rejects_a_missing_plan_id()
    {
        var text = ValidNote.Replace("plan_id: recipe-tagging\n", "");

        var ex = Assert.Throws<VaultSchemaException>(() => ChildNoteParser.Parse(text, "01-tag-entity.md"));

        Assert.Contains("missing required key 'plan_id'", ex.Message);
    }

    [Fact]
    public void Rejects_a_malformed_checkpoint_id()
    {
        var text = ValidNote.Replace("checkpoint_id: 01-tag-entity", "checkpoint_id: TagEntity");

        var ex = Assert.Throws<VaultSchemaException>(() => ChildNoteParser.Parse(text, "01-tag-entity.md"));

        Assert.Contains("must match", ex.Message);
    }
}
