using System.Text.Json;
using Orca.Core.Execution;

namespace Orca.Tests;

public class RunLogLayoutTests
{
    [Fact]
    public void Builds_both_attempt_paths_under_orca_runs()
    {
        var layout = new RunLogLayout("/repo");
        var paths = layout.AttemptPaths("recipe-tagging", "01-tag-entity", 2);

        var expectedDir = Path.Combine(Path.GetFullPath("/repo"), ".orca", "runs", "recipe-tagging");
        Assert.Equal(Path.Combine(expectedDir, "01-tag-entity.attempt-2.ndjson"), paths.EventLog);
        Assert.Equal(Path.Combine(expectedDir, "01-tag-entity.attempt-2.stderr.txt"), paths.Stderr);
    }

    [Theory]
    [InlineData("Recipe-Tagging", "01-tag-entity")] // uppercase plan id
    [InlineData("../escape", "01-tag-entity")]      // traversal in plan id
    [InlineData("recipe-tagging", "tag-entity")]    // checkpoint id without NN- prefix
    [InlineData("recipe-tagging", "01-Tag")]        // uppercase checkpoint id
    public void Rejects_ids_that_fail_the_schema_regexes(string planId, string checkpointId) =>
        Assert.ThrowsAny<ArgumentException>(() =>
            new RunLogLayout("/repo").AttemptPaths(planId, checkpointId, 1));

    [Fact]
    public void Rejects_a_non_positive_attempt() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RunLogLayout("/repo").AttemptPaths("recipe-tagging", "01-tag-entity", 0));

    [Fact]
    public void Planning_attempts_live_beside_the_checkpoint_logs()
    {
        var layout = new RunLogLayout("/repo");
        var paths = layout.PlanningAttemptPaths("recipe-tagging", 3);

        // The "planning" stem cannot collide with a checkpoint id (those always start
        // with a two-digit prefix).
        var expectedDir = Path.Combine(Path.GetFullPath("/repo"), ".orca", "runs", "recipe-tagging");
        Assert.Equal(Path.Combine(expectedDir, "planning.attempt-3.ndjson"), paths.EventLog);
        Assert.Equal(Path.Combine(expectedDir, "planning.attempt-3.stderr.txt"), paths.Stderr);
        Assert.Equal(
            Path.Combine(expectedDir, "planning.attempt-3.settings.json"),
            layout.PlanningSettingsFile("recipe-tagging", 3));
    }

    [Fact]
    public void Planning_paths_hold_the_same_id_and_attempt_discipline()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new RunLogLayout("/repo").PlanningAttemptPaths("../escape", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RunLogLayout("/repo").PlanningAttemptPaths("recipe-tagging", 0));
        Assert.ThrowsAny<ArgumentException>(() =>
            new RunLogLayout("/repo").PlanningSettingsFile("../escape", 1));
    }

    [Fact]
    public void Gate_output_files_sit_beside_the_attempt_logs()
    {
        var layout = new RunLogLayout("/repo");
        var expectedDir = Path.Combine(Path.GetFullPath("/repo"), ".orca", "runs", "recipe-tagging");

        Assert.Equal(
            Path.Combine(expectedDir, "01-tag-entity.attempt-2.build.txt"),
            layout.GateOutputFile("recipe-tagging", "01-tag-entity", 2, "build"));
        Assert.Equal(
            Path.Combine(expectedDir, "01-tag-entity.attempt-2.tests.txt"),
            layout.GateOutputFile("recipe-tagging", "01-tag-entity", 2, "tests"));
        Assert.ThrowsAny<ArgumentException>(() =>
            layout.GateOutputFile("recipe-tagging", "01-tag-entity", 2, "lint"));
    }

    [Fact]
    public void Role_invocations_number_their_parse_retries_within_an_attempt()
    {
        var layout = new RunLogLayout("/repo");
        var expectedDir = Path.Combine(Path.GetFullPath("/repo"), ".orca", "runs", "recipe-tagging");

        var paths = layout.RoleAttemptPaths("recipe-tagging", "01-tag-entity", 2, "reconciler", 3);
        Assert.Equal(Path.Combine(expectedDir, "01-tag-entity.attempt-2.reconciler-3.ndjson"), paths.EventLog);
        Assert.Equal(Path.Combine(expectedDir, "01-tag-entity.attempt-2.reconciler-3.stderr.txt"), paths.Stderr);
        Assert.Equal(
            Path.Combine(expectedDir, "01-tag-entity.attempt-2.verifier-1.settings.json"),
            layout.RoleSettingsFile("recipe-tagging", "01-tag-entity", 2, "verifier", 1));

        Assert.ThrowsAny<ArgumentException>(() =>
            layout.RoleAttemptPaths("recipe-tagging", "01-tag-entity", 2, "planner", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            layout.RoleAttemptPaths("recipe-tagging", "01-tag-entity", 2, "verifier", 0));
    }
}

public class RunLogWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("orca-runlog-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Writes_lines_verbatim_and_flushes_each_one()
    {
        var path = Path.Combine(_dir, "log", "chk.attempt-1.ndjson");
        using var writer = RunLogWriter.Create(path);

        writer.WriteLine("{\"type\":\"system\"}");
        writer.WriteLine("{\"type\":\"result\"}");

        // Read while the writer is still open: proves flush-per-line, not flush-on-dispose.
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        Assert.Equal("{\"type\":\"system\"}\n{\"type\":\"result\"}\n", reader.ReadToEnd());
    }

    [Fact]
    public void Refuses_to_overwrite_an_existing_attempt_log()
    {
        var path = Path.Combine(_dir, "chk.attempt-1.ndjson");
        File.WriteAllText(path, "prior attempt evidence");

        var e = Assert.Throws<ExecutorException>(() => RunLogWriter.Create(path));

        Assert.Contains("refusing to overwrite", e.Message);
        Assert.Equal("prior attempt evidence", File.ReadAllText(path));
    }

    [Fact]
    public void Executor_events_are_namespaced_and_snake_cased()
    {
        var path = Path.Combine(_dir, "chk.attempt-2.ndjson");
        using (var writer = RunLogWriter.Create(path))
        {
            writer.WriteExecutorEvent("timeout_kill", new Dictionary<string, object?>
            {
                ["timeout_ms"] = 250L,
                ["elapsed_ms"] = 291L,
            });
        }

        using var document = JsonDocument.Parse(File.ReadAllLines(path).Single());
        var root = document.RootElement;

        Assert.Equal("orca_executor", root.GetProperty("type").GetString());
        Assert.Equal("timeout_kill", root.GetProperty("subtype").GetString());
        Assert.Equal(250, root.GetProperty("timeout_ms").GetInt64());
        Assert.True(root.TryGetProperty("timestamp", out _));
    }
}
