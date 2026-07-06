namespace Orca.Core.Execution;

/// <summary>Executor contract violation: a missing CLI binary, an invalid invocation
/// option, a refused run-log overwrite.</summary>
public class ExecutorException : Exception
{
    public ExecutorException(string message)
        : base(message)
    {
    }

    public ExecutorException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>A stream-json line that is not valid JSON, or that is missing a field Orca
/// consumes. Always thrown, never downgraded: a half-parsed transcript silently accepted
/// is exactly the drift Orca exists to prevent. Unknown event <em>types</em> are not a
/// parse failure — the union is open by design (see the transcript fixtures).</summary>
public sealed class StreamParseException : ExecutorException
{
    public StreamParseException(string line, string message)
        : base($"{message} — line: {Truncate(line)}")
    {
        Line = line;
    }

    public StreamParseException(string line, string message, Exception inner)
        : base($"{message} — line: {Truncate(line)}", inner)
    {
        Line = line;
    }

    /// <summary>The raw NDJSON line as received (it is also already in the run log —
    /// the executor logs before it parses).</summary>
    public string Line { get; }

    private static string Truncate(string line) =>
        line.Length <= 200 ? line : line[..200] + "…";
}
