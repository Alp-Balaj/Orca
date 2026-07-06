using Orca.Core.Domain;
using Orca.Core.Vault;

namespace Orca.Tests;

public sealed class VaultIoTests : IDisposable
{
    private const string PlanText = """
        ---
        schema_version: 1
        plan_id: recipe-tagging
        plan_version: 1
        title: Add tagging to recipes
        status: approved
        target_repo: RecipeApp
        base_branch: main
        created: 2026-07-06T14:00:00Z
        updated: 2026-07-06T14:00:00Z
        planning:
          strategy: single_candidate
        checkpoints:
          - id: 01-tag-entity
            description: Add Tag entity + EF mapping
            status: pending
            acceptance_criteria:
              - it works
        ---
        Plan body.
        """;

    private const string ChildNoteText = """
        ---
        schema_version: 1
        plan_id: recipe-tagging
        checkpoint_id: 01-tag-entity
        ---
        Detailed instructions for the Implementer.
        """;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "orca-tests-" + Guid.NewGuid().ToString("N"));

    public VaultIoTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private VaultLayout Layout(string plansDir = "Plans") => new(_root, plansDir);

    private void WriteVaultFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static VerificationRecord FailedGatesRecord(int attempt) => new(
        SchemaVersion: 1,
        PlanId: "recipe-tagging",
        PlanVersion: 1,
        CheckpointId: "01-tag-entity",
        Attempt: attempt,
        Timestamp: new DateTimeOffset(2026, 7, 6, 15, 20, 0, TimeSpan.Zero),
        Gates: new GateResults(GateOutcome.Pass, GateOutcome.Fail),
        Semantic: null,
        Verdict: VerificationVerdict.Fail,
        Body: "Failing tests: TagRepositoryTests.Unique_index_is_enforced.\n");

    [Fact]
    public void Reads_a_plan_from_a_nested_plans_directory()
    {
        WriteVaultFile("Projects/RecipeApp/Plans/recipe-tagging/recipe-tagging.md", PlanText);
        var reader = new VaultReader(Layout("Projects/RecipeApp/Plans"));

        var plan = reader.ReadPlan("recipe-tagging");

        Assert.Equal("recipe-tagging", plan.PlanId);
        Assert.Single(plan.Checkpoints);
    }

    [Fact]
    public void Throws_when_the_plan_file_is_missing()
    {
        var reader = new VaultReader(Layout());

        var ex = Assert.Throws<VaultException>(() => reader.ReadPlan("recipe-tagging"));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void Reads_a_child_note_for_a_known_checkpoint()
    {
        WriteVaultFile("Plans/recipe-tagging/recipe-tagging.md", PlanText);
        WriteVaultFile("Plans/recipe-tagging/01-tag-entity.md", ChildNoteText);
        var reader = new VaultReader(Layout());

        var note = reader.ReadChildNote(reader.ReadPlan("recipe-tagging"), "01-tag-entity");

        Assert.Contains("Detailed instructions", note.Body);
    }

    [Fact]
    public void Refuses_a_checkpoint_id_the_plan_does_not_declare()
    {
        WriteVaultFile("Plans/recipe-tagging/recipe-tagging.md", PlanText);
        var reader = new VaultReader(Layout());

        var ex = Assert.Throws<VaultException>(
            () => reader.ReadChildNote(reader.ReadPlan("recipe-tagging"), "02-unknown"));

        Assert.Contains("has no checkpoint", ex.Message);
    }

    [Fact]
    public void Refuses_a_child_note_that_claims_a_different_plan()
    {
        WriteVaultFile("Plans/recipe-tagging/recipe-tagging.md", PlanText);
        WriteVaultFile(
            "Plans/recipe-tagging/01-tag-entity.md",
            ChildNoteText.Replace("plan_id: recipe-tagging", "plan_id: some-other-plan"));
        var reader = new VaultReader(Layout());

        var ex = Assert.Throws<VaultSchemaException>(
            () => reader.ReadChildNote(reader.ReadPlan("recipe-tagging"), "01-tag-entity"));

        Assert.Contains("refusing to assemble mismatched instructions", ex.Message);
    }

    [Fact]
    public void Writes_a_verification_record_and_reads_it_back()
    {
        var layout = Layout();
        var reader = new VaultReader(layout);
        var writer = new VaultWriter(layout);
        Assert.Empty(reader.ReadVerificationRecords("recipe-tagging", "01-tag-entity"));
        Assert.Equal(1, reader.NextAttemptNumber("recipe-tagging", "01-tag-entity"));

        var record = FailedGatesRecord(attempt: 1);
        var path = writer.WriteVerificationRecord(record);

        Assert.True(File.Exists(path));
        Assert.EndsWith("01-tag-entity.attempt-1.md", path);
        var restored = Assert.Single(reader.ReadVerificationRecords("recipe-tagging", "01-tag-entity"));
        Assert.Equal(
            VerificationRecordSerializer.Serialize(record),
            VerificationRecordSerializer.Serialize(restored));
        Assert.Equal(2, reader.NextAttemptNumber("recipe-tagging", "01-tag-entity"));
    }

    [Fact]
    public void Refuses_to_overwrite_an_existing_attempt()
    {
        var writer = new VaultWriter(Layout());
        writer.WriteVerificationRecord(FailedGatesRecord(attempt: 1));

        var ex = Assert.Throws<VaultException>(
            () => writer.WriteVerificationRecord(FailedGatesRecord(attempt: 1)));

        Assert.Contains("append-only", ex.Message);
    }

    [Fact]
    public void Returns_records_ordered_by_attempt_regardless_of_write_order()
    {
        var layout = Layout();
        var writer = new VaultWriter(layout);
        writer.WriteVerificationRecord(FailedGatesRecord(attempt: 2));
        writer.WriteVerificationRecord(FailedGatesRecord(attempt: 1));

        var records = new VaultReader(layout).ReadVerificationRecords("recipe-tagging", "01-tag-entity");

        Assert.Equal([1, 2], records.Select(r => r.Attempt));
    }

    [Fact]
    public void Rejects_an_inconsistent_record_before_touching_the_filesystem()
    {
        var writer = new VaultWriter(Layout());
        var inconsistent = FailedGatesRecord(attempt: 1) with { Verdict = VerificationVerdict.Pass };

        Assert.Throws<VaultSchemaException>(() => writer.WriteVerificationRecord(inconsistent));
        Assert.False(Directory.Exists(Layout().VerificationDirectory("recipe-tagging")));
    }

    [Fact]
    public void Layout_rejects_ids_that_could_escape_the_plans_directory()
    {
        var layout = Layout();

        Assert.Throws<ArgumentException>(() => layout.PlanFile("../evil"));
        Assert.Throws<ArgumentException>(() => layout.ChildNoteFile("recipe-tagging", "../../evil"));
        Assert.Throws<ArgumentException>(() => new VaultLayout(_root, "../outside"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => layout.VerificationFile("recipe-tagging", "01-tag-entity", 0));
    }
}
