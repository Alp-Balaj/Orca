using System.ComponentModel;
using System.Diagnostics;
using Orca.Core.Domain;

namespace Orca.Core.Run;

/// <summary>A gate could not run at all — a missing command binary is an environment
/// problem to fix, not a fact about the diff, so it never records as a gate failure.</summary>
public class GateException : Exception
{
    public GateException(string message) : base(message) { }
    public GateException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Result of one deterministic gate command. <see cref="Outcome"/> is derived:
/// exit 0 without a timeout kill is a pass, anything else is a fail — no interpretation
/// beyond what the machine said.</summary>
public sealed record GateCommandResult
{
    /// <summary>"build" or "tests".</summary>
    public required string Gate { get; init; }

    public required IReadOnlyList<string> Command { get; init; }
    public required int ExitCode { get; init; }
    public required bool TimedOut { get; init; }

    /// <summary>Where the full combined stdout+stderr capture went
    /// (<c>.orca/runs/…</c> — evidence, never the vault).</summary>
    public required string OutputPath { get; init; }

    /// <summary>The last <see cref="GateRunner.TailLineCount"/> output lines — what the
    /// verification record's body excerpts on failure and the Verifier prompt embeds.</summary>
    public required string OutputTail { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public GateOutcome Outcome => !TimedOut && ExitCode == 0 ? GateOutcome.Pass : GateOutcome.Fail;
}

/// <summary>Runs the configured build/test commands as plain process calls (spec §8: no
/// LLM is involved in establishing whether the build compiles or the tests pass). Argv
/// arrays, no shell — the <c>GitProcess</c> philosophy. Combined output streams to the
/// run-log file line-by-line as it arrives, so a killed or hung gate still leaves its
/// evidence on disk.</summary>
public sealed class GateRunner(TimeSpan? timeout)
{
    /// <summary>Excerpt size for record bodies and Verifier prompts. Presentation, not
    /// policy — the full output is always in the run log.</summary>
    public const int TailLineCount = 60;

    public GateCommandResult Run(
        string gate, IReadOnlyList<string> command, string workingDirectory, string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (command.Count == 0)
            throw new GateException($"gate '{gate}' has an empty command — check commands.{gate} in orca.json");

        // BF-05: a pre-cancelled token must never spawn the gate at all.
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var startInfo = new ProcessStartInfo
        {
            FileName = command[0],
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in command.Skip(1))
            startInfo.ArgumentList.Add(argument);

        var stopwatch = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new GateException($"gate '{gate}' failed to start: {command[0]}");
        }
        catch (Win32Exception e)
        {
            throw new GateException(
                $"gate '{gate}' command '{command[0]}' could not start — is it installed and on PATH?", e);
        }

        using (process)
        using (var output = new StreamWriter(outputPath, append: false))
        {
            process.StandardInput.Close();

            var tail = new Queue<string>(TailLineCount);
            var sink = new object();
            void Capture(string? line)
            {
                if (line is null)
                    return;
                lock (sink)
                {
                    output.WriteLine(line);
                    output.Flush();
                    if (tail.Count == TailLineCount)
                        tail.Dequeue();
                    tail.Enqueue(line);
                }
            }

            process.OutputDataReceived += (_, e) => Capture(e.Data);
            process.ErrorDataReceived += (_, e) => Capture(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // BF-05: the wait polls so an interrupt actually reaches a running gate —
            // Ctrl-C used to claim "process tree killed" while dotnet test ran to
            // completion regardless. Cancellation kills the tree and throws; the note
            // lands in the output log first so the evidence explains the short run.
            var timedOut = false;
            var cancelled = false;
            var deadline = timeout is { } ceiling ? stopwatch.Elapsed + ceiling : (TimeSpan?)null;
            while (!process.WaitForExit(100))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    process.Kill(entireProcessTree: true);
                    break;
                }

                if (deadline is { } limit && stopwatch.Elapsed > limit)
                {
                    timedOut = true;
                    process.Kill(entireProcessTree: true);
                    break;
                }
            }

            // Unconditional wait drains the async output readers even after a kill.
            process.WaitForExit();
            stopwatch.Stop();

            if (cancelled)
            {
                lock (sink)
                {
                    output.WriteLine($"[orca] gate '{gate}' interrupted — process tree killed");
                    output.Flush();
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            lock (sink)
            {
                if (timedOut)
                {
                    var note = $"[orca] gate '{gate}' did not finish within {timeout!.Value.TotalSeconds:0}s — process tree killed";
                    output.WriteLine(note);
                    output.Flush();
                    if (tail.Count == TailLineCount)
                        tail.Dequeue();
                    tail.Enqueue(note);
                }

                return new GateCommandResult
                {
                    Gate = gate,
                    Command = command,
                    ExitCode = process.ExitCode,
                    TimedOut = timedOut,
                    OutputPath = outputPath,
                    OutputTail = string.Join('\n', tail),
                    Elapsed = stopwatch.Elapsed,
                };
            }
        }
    }
}
