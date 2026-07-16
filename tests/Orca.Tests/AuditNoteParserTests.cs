using Orca.Core.Vault;

namespace Orca.Tests;

/// <summary>SUG-01: the Scout's audit note — strict frontmatter, contracted section
/// headings, opaque prose content.</summary>
public sealed class AuditNoteParserTests
{
    private const string Path = "/vault/Plans/recipe-tagging/recipe-tagging.audit.md";

    private const string Valid =
        """
        ---
        schema_version: 1
        type: audit
        plan_id: recipe-tagging
        created: 2026-07-17T09:00:00Z
        ---

        ## Verified current state

        .NET 10 API, tags table absent (checked src/Db/AppDbContext.cs).

        ## Key files and patterns

        Controllers under src/Api/Controllers, one file per resource.

        ## Dependencies

        EF Core 10.0.2 pinned in Directory.Packages.props.

        ## Risks

        Migration folder has a half-applied migration (20260701_AddNotes).

        ## Suggested checkpoint boundaries

        Entity+migration first (proves the migration risk), then endpoints, then UI wiring.
        """;

    [Fact]
    public void Parses_a_contract_conforming_audit()
    {
        var note = AuditNoteParser.Parse(Valid, Path);

        Assert.Equal("recipe-tagging", note.PlanId);
        Assert.Equal(new DateTimeOffset(2026, 7, 17, 9, 0, 0, TimeSpan.Zero), note.Created);
        Assert.Contains("half-applied migration", note.Body);
    }

    [Fact]
    public void Layout_puts_the_audit_next_to_the_plan_file()
    {
        var layout = new VaultLayout("/vault", "Plans");
        Assert.Equal(
            System.IO.Path.Combine("/vault", "Plans", "recipe-tagging", "recipe-tagging.audit.md"),
            layout.AuditFile("recipe-tagging"));
    }

    [Fact]
    public void Rejects_a_wrong_type()
    {
        var ex = Assert.Throws<VaultSchemaException>(() =>
            AuditNoteParser.Parse(Valid.Replace("type: audit", "type: plan"), Path));
        Assert.Contains("audit", ex.Message);
    }

    [Fact]
    public void Rejects_a_plan_id_that_does_not_match_the_file_stem()
    {
        var ex = Assert.Throws<VaultSchemaException>(() =>
            AuditNoteParser.Parse(Valid, "/vault/Plans/other/other.audit.md"));
        Assert.Contains("file name stem", ex.Message);
    }

    [Fact]
    public void Rejects_a_missing_contracted_section()
    {
        var ex = Assert.Throws<VaultSchemaException>(() =>
            AuditNoteParser.Parse(Valid.Replace("## Risks", "## Hazards"), Path));
        Assert.Contains("'## Risks'", ex.Message);
    }

    [Fact]
    public void Rejects_an_empty_body()
    {
        var frontmatterOnly = Valid[..(Valid.IndexOf("---", 4, StringComparison.Ordinal) + 3)] + "\n\n";
        var ex = Assert.Throws<VaultSchemaException>(() => AuditNoteParser.Parse(frontmatterOnly, Path));
        Assert.Contains("empty", ex.Message);
    }

    [Fact]
    public void Rejects_unknown_frontmatter_keys()
    {
        var ex = Assert.Throws<VaultSchemaException>(() =>
            AuditNoteParser.Parse(Valid.Replace("type: audit", "type: audit\nseverity: high"), Path));
        Assert.Contains("severity", ex.Message);
    }
}
