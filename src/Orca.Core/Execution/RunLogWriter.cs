using System.Text.Json;

namespace Orca.Core.Execution;

/// <summary>Append-as-received NDJSON run-log writer.
/// <para><b>Log-before-parse:</b> the executor writes each raw line and flushes before
/// attempting to parse it, so the log is complete evidence even if parsing throws or the
/// process dies mid-stream. Refuses to overwrite an existing attempt log — attempt
/// history is append-only, same discipline as verification records.</para>
/// <para>Executor-side facts (timeout kill, process exit) are appended as namespaced
/// <c>orca_executor</c> events so they can never be mistaken for CLI output.</para></summary>
public sealed class RunLogWriter : IDisposable
{
    private readonly StreamWriter _writer;

    private RunLogWriter(StreamWriter writer)
    {
        _writer = writer;
    }

    public static RunLogWriter Create(string path)
    {
        if (File.Exists(path))
            throw new ExecutorException($"run log already exists, refusing to overwrite: {path}");

        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new ExecutorException($"run log path has no directory: {path}"));

        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        return new RunLogWriter(new StreamWriter(stream) { NewLine = "\n" });
    }

    /// <summary>Appends one raw line exactly as received and flushes immediately.</summary>
    public void WriteLine(string rawLine)
    {
        _writer.WriteLine(rawLine);
        _writer.Flush();
    }

    /// <summary>Appends an executor fact as <c>{"type":"orca_executor","subtype":…}</c>.
    /// Field keys are snake_case to match the CLI's own dialect in the same file.</summary>
    public void WriteExecutorEvent(string subtype, IReadOnlyDictionary<string, object?> fields)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "orca_executor",
            ["subtype"] = subtype,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        foreach (var (key, value) in fields)
            payload[key] = value;

        WriteLine(JsonSerializer.Serialize(payload));
    }

    public void Dispose() => _writer.Dispose();
}
