using Orca.Core.Domain;
using Orca.Core.Vault;

namespace Orca.Tests;

public sealed class CheckpointStatusEditorTests : IDisposable
{
    // Two checkpoints, CRLF body with a blank line and prose, so a byte-diff of an
    // untouched copy can prove only the intended lines moved.
    private const string PlanText =
        "---\r\n" +
        "schema_version: 1\r\n" +
        "plan_id: recipe-tagging\r\n" +
        "plan_version: 1\r\n" +
        "title: Add tagging to recipes\r\n" +
        "status: approved\r\n" +
        "target_repo: RecipeApp\r\n" +
        "base_branch: main\r\n" +
        "created: 2026-07-06T14:00:00Z\r\n" +
        "updated: 2026-07-06T14:00:00Z\r\n" +
        "planning:\r\n" +
        "  strategy: single_candidate\r\n" +
        "checkpoints:\r\n" +
        "  - id: 01-tag-entity\r\n" +
        "    description: Add Tag entity + EF mapping\r\n" +
        "    status: complete\r\n" +
        "    acceptance_criteria:\r\n" +
        "      - it works\r\n" +
        "  - id: 02-tag-endpoint\r\n" +
        "    description: Expose the tagging endpoint\r\n" +
        "    status: pending\r\n" +
        "    acceptance_criteria:\r\n" +
        "      - it works too\r\n" +
        "---\r\n" +
        "\r\n" +
        "<!-- Plan body: free prose for the human. -->\r\n" +
        "\r\n" +
        "## Context\r\n" +
        "\r\n" +
        "Some human prose that must survive untouched.\r\n";

    private static readonly DateTimeOffset Updated = new(2026, 7, 16, 9, 30, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "orca-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _planPath;

    public CheckpointStatusEditorTests()
    {
        Directory.CreateDirectory(_root);
        _planPath = Path.Combine(_root, "recipe-tagging.md");
        File.WriteAllText(_planPath, PlanText);
    }

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

    private static string[] SplitKeepEmpty(string text) => text.Split('\n');

    [Fact]
    public void Flips_exactly_the_target_checkpoint_status_line_and_the_updated_line()
    {
        CheckpointStatusEditor.SetStatus(
            _planPath, "02-tag-endpoint", CheckpointStatus.Pending, CheckpointStatus.InProgress, Updated);

        var before = SplitKeepEmpty(PlanText);
        var after = SplitKeepEmpty(File.ReadAllText(_planPath));

        Assert.Equal(before.Length, after.Length);
        var changedLines = new List<int>();
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i] != after[i])
                changedLines.Add(i);
        }

        Assert.Equal(["updated: 2026-07-16T09:30:00Z\r", "    status: in_progress\r"],
            changedLines.Select(i => after[i]));
    }

    [Fact]
    public void Does_not_touch_the_other_checkpoints_status()
    {
        CheckpointStatusEditor.SetStatus(
            _planPath, "02-tag-endpoint", CheckpointStatus.Pending, CheckpointStatus.InProgress, Updated);

        var plan = PlanParser.Parse(File.ReadAllText(_planPath), _planPath);
        Assert.Equal(CheckpointStatus.Complete, plan.Checkpoints[0].Status);
        Assert.Equal(CheckpointStatus.InProgress, plan.Checkpoints[1].Status);
    }

    [Fact]
    public void Preserves_the_body_byte_exact_including_crlf_and_prose()
    {
        var bodyBefore = PlanText[(PlanText.IndexOf("---\r\n\r\n", StringComparison.Ordinal) + 5)..];

        CheckpointStatusEditor.SetStatus(
            _planPath, "01-tag-entity", CheckpointStatus.Complete, CheckpointStatus.Rejected, Updated);

        var after = File.ReadAllText(_planPath);
        var bodyAfter = after[(after.IndexOf("---\r\n\r\n", StringComparison.Ordinal) + 5)..];
        Assert.Equal(bodyBefore, bodyAfter);
    }

    [Fact]
    public void Flips_the_plan_status_only_when_asked()
    {
        CheckpointStatusEditor.SetStatus(
            _planPath, "02-tag-endpoint", CheckpointStatus.Pending, CheckpointStatus.Complete, Updated,
            new PlanStatusChange(PlanStatus.Approved, PlanStatus.Complete));

        Assert.Contains("\r\nstatus: complete\r\n", File.ReadAllText(_planPath));
    }

    [Fact]
    public void Leaves_the_plan_status_line_untouched_when_not_requested()
    {
        CheckpointStatusEditor.SetStatus(
            _planPath, "02-tag-endpoint", CheckpointStatus.Pending, CheckpointStatus.Complete, Updated);

        Assert.Contains("\r\nstatus: approved\r\n", File.ReadAllText(_planPath));
    }

    [Fact]
    public void Throws_naming_the_missing_checkpoint_id()
    {
        var ex = Assert.Throws<VaultException>(() => CheckpointStatusEditor.SetStatus(
            _planPath, "99-does-not-exist", CheckpointStatus.Pending, CheckpointStatus.InProgress, Updated));

        Assert.Contains("99-does-not-exist", ex.Message);
    }

    [Fact]
    public void Throws_when_the_expected_current_status_does_not_match()
    {
        var ex = Assert.Throws<VaultException>(() => CheckpointStatusEditor.SetStatus(
            _planPath, "02-tag-endpoint", CheckpointStatus.InProgress, CheckpointStatus.Complete, Updated));

        Assert.Contains("pending", ex.Message);
        Assert.Contains("in_progress", ex.Message);
    }

    [Fact]
    public void Throws_when_the_expected_current_plan_status_does_not_match()
    {
        var ex = Assert.Throws<VaultException>(() => CheckpointStatusEditor.SetStatus(
            _planPath, "02-tag-endpoint", CheckpointStatus.Pending, CheckpointStatus.Complete, Updated,
            new PlanStatusChange(PlanStatus.Draft, PlanStatus.Complete)));

        Assert.Contains("approved", ex.Message);
        Assert.Contains("draft", ex.Message);
    }

    [Fact]
    public void No_temp_file_remains_after_a_successful_edit()
    {
        CheckpointStatusEditor.SetStatus(
            _planPath, "02-tag-endpoint", CheckpointStatus.Pending, CheckpointStatus.InProgress, Updated);

        Assert.DoesNotContain(Directory.EnumerateFiles(_root), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void No_temp_file_remains_after_a_failed_edit()
    {
        Assert.Throws<VaultException>(() => CheckpointStatusEditor.SetStatus(
            _planPath, "99-does-not-exist", CheckpointStatus.Pending, CheckpointStatus.InProgress, Updated));

        Assert.DoesNotContain(Directory.EnumerateFiles(_root), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }
}
