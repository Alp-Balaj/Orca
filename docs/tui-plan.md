> **Provenance:** TUI feature design + build plan, drafted 2026-07-09 from the owner's
> scoping notes, after a full survey of the current code. Per the working agreement,
> the public contracts in §3 are **proposals awaiting owner approval** — no code depends
> on them yet, and nothing in this document is implemented. Written to the repo
> (`docs/tui-plan.md`) rather than the vault deliberately: the vault's `Plans/` tree is a
> machine-parsed namespace (PlanLocator/PlanParser), and a design doc that is not a
> schema-valid plan doesn't belong in it. Once §8 is decided, the real vault plan should
> be produced the Orca way (`plan-init`/`plan-generate` + owner approval), with this
> document as its source material.

# Orca TUI — full-screen interactive front end

Orca gains a full-screen, reactive terminal UI as its default entry point: `orca` with no
arguments opens it, every existing subcommand keeps working unchanged for scripting. The
flagship capability — the reason full-screen earns its complexity over a menu — is live
observability into the one invocation already in flight: the NDJSON stream
`ClaudeCodeExecutor` already parses becomes visible as it happens, for all four roles,
instead of landing silently in a run-log file you read after the wait.

## 0. What this adds, and what it must not change

The TUI is **observability and ergonomics over the existing workflow — not more
autonomy**. Every invariant that defines Orca survives verbatim:

- One scoped `claude -p` invocation per checkpoint attempt (spec §6). The live pane is a
  read-only window into that one invocation; it adds no control channel.
- A human decides at every gate. The approve/reject/defer prompt becomes a modal dialog;
  it is the same gate, never defaulted, never auto-advanced.
- Nothing runs unattended across checkpoint boundaries. The TUI runs at most one session
  at a time, modally: while a checkpoint or planning run is active, the run screen owns
  the UI, and when it ends the workflow stops until the human explicitly triggers the
  next action. There is no "run all checkpoints" affordance, and no background work
  continues between screens.
- Orca never commits to the human's branch on its own; the `.orca/` gitignore guard,
  dirty-tree refusal, and attempt ceilings all keep firing — enforced once, in shared
  code (§3.3), not per front end.

## 1. Framework: Terminal.Gui v2 — confirmed, with evidence

**Decision: Terminal.Gui, pinned at 2.4.17.** Verified against NuGet on 2026-07-09:

- **v2 is stable now** — 2.4.17 is a stable release (the years of `2.0.0-develop.*`
  prerelease are over; the strongest historical objection to v2 is gone). MIT-licensed,
  repo moved to `tui-cs/Terminal.Gui`.
- **Targets `net10.0` exactly** — matches this repo's TFM; no compatibility shims.
- **The main-loop marshaling primitive exists and is verified**:
  `Terminal.Gui.App.Application.Invoke(Action)` is present in the shipped 2.4.17
  assembly (checked in its XML docs), alongside `Application.AddTimeout` — precisely
  what pushing executor events from a background thread into the widget tree needs.
- Transitive dependencies it brings: ColorHelper, Markdig, TextMateSharp(+Grammars),
  Wcwidth, System.IO.Abstractions, several Microsoft.Extensions.* packages. Moderate,
  all MIT-compatible; they ride only `Orca.Cli`, never `Orca.Core`.

**Why not Spectre.Console**, which is already a dependency: it is a render-and-print
library. It has an alternate-screen helper and Live Display, but no persistent widget
tree, no cross-pane focus routing, no modal dialog system, and no main-loop `Invoke` —
a tree you can navigate *while* a log pane streams *while* a status bar updates is
exactly the shape it cannot host. Spectre stays, unchanged, as the subcommand lane.

**Alternatives rejected:** hand-rolled ANSI (reinvents a TUI framework badly);
Consolonia (Avalonia-derived, heavier, wrong idiom for a CLI tool); Terminal.Gui v1
(maintenance mode; v2's API and driver model are the ones with a future).

**Risk accepted and mitigated:** Terminal.Gui's minor-version cadence is fast and v2
APIs still move between minors. Mitigation is the same discipline as the Claude CLI pin:
pin 2.4.17 in `Directory.Packages.props`, record it in the README pinned-tools table,
upgrade deliberately. The TUI layer stays thin (§4), so churn touches presentation only.

## 2. Architecture: one implementation, two front ends

The survey corrected the scoping note's assumption: the business logic does **not** live
in `Orca.Cli/Commands/*.cs` today. Both flows already follow a pure-`Prepare` →
mutating-`RunAsync`/`GenerateAsync` split in Core (`CheckpointRunner`,
`CheckpointVerifier`, `SingleCandidatePlanningStrategy`). What the commands actually own
is thinner, and that is what the extraction must reify:

| Concern | Today | After |
| --- | --- | --- |
| Run/verify/plan mechanics | Core services (done) | unchanged |
| Sub-step progress | invisible (run-log file only) | `IRunObserver` events (§3.2) fed by Core |
| Per-event stream | invisible until the log is read | executor observer hook (§3.1) |
| Flow control (pending-decision resume, gitignore guard, failure branching, decide) | ~100 lines inside `CheckpointRunCommand` | `CheckpointRunSession` in Core (§3.3) |
| The human gate | `AnsiConsole.Prompt` calls inline | `ICheckpointGate` interface (§3.3); Spectre and TUI each implement it |
| Status assembly | inline in `StatusCommand` | `StatusReport` query in Core (§3.4) |
| Rendering | Spectre tables/panels | per-front-end (unchanged CLI output; TUI views) |

**The lanes rule:** Spectre.Console and Terminal.Gui never own the terminal at the same
time. Spectre.Console.Cli remains the argument parser for everything; the TUI is entered
either before Spectre dispatch (bare `orca`) or via a subcommand that immediately hands
the terminal to Terminal.Gui and touches `AnsiConsole` never again until shutdown.
`Orca.Core` references neither library — true today, an explicit rule after.

## 3. Public contracts proposed for approval

These are the contracts new code will depend on. Per the working agreement they need
explicit owner approval **before** step 1 of §6 starts. Signatures are written against
the real current types (`ClaudeStreamEvent`, `GateCommandResult`, `CheckpointRunPreview`,
`RoleName`, …) and are intended to compile as written.

### 3.1 Executor observability — `IClaudeStreamObserver`

```csharp
namespace Orca.Core.Execution;

/// <summary>Read-only, per-invocation observer of the parsed stream. Called once per
/// event, in arrival order, on the executor's stdout pump thread — never the caller's.
/// Implementations must return quickly and must not throw: an observer exception is
/// caught, recorded once in the run log as an orca_executor/observer_failure event, and
/// the invocation continues unharmed — the run is never hostage to its audience.</summary>
public interface IClaudeStreamObserver
{
    void OnStreamEvent(ClaudeStreamEvent streamEvent);
}

public interface IClaudeExecutor
{
    Task<ClaudeExecutionResult> ExecuteAsync(
        ClaudeInvocationOptions options,
        RunLogPaths logPaths,
        IClaudeStreamObserver? observer = null,
        CancellationToken cancellationToken = default);
}
```

Semantics, unchanged from today unless stated:

- **Log-before-parse stands.** The raw line is appended to the run log first; the
  observer sees the *parsed* event afterwards. The observer is a second sink beside the
  log, never a replacement and never a gate — nothing downstream consumes observer
  state as evidence.
- Unparseable lines keep failing the invocation loudly at process exit exactly as today;
  the observer simply never sees them (the pane shows the loud failure via §3.2 instead).
- Ordering guarantee: `ExecuteAsync` awaits the pump before returning, so all observer
  calls happen-before the returned result — a caller can safely emit its own
  "finished" event after awaiting without racing stream events.
- This is deliberately a tiny interface rather than `IProgress<T>`: the BCL
  `Progress<T>` posts to a captured SynchronizationContext (or the thread pool), which
  neither preserves ordering nor exists in this app — a transcript view with reordered
  events would be worse than none.
- Existing `ExecuteAsync(options, logPaths, ct)` call sites pass `ct` positionally and
  will not compile against the new signature; they are updated in the same step, along
  with the test fakes. That loud break is preferable to an overload that lets a caller
  silently miss the seam.

### 3.2 Role-tagged run progress — `IRunObserver` + `RunObservation`

The executor doesn't know which role it is running; callers do. Core services tag and
forward, and add the pipeline facts the executor can't know (gates, retries, branch,
records):

```csharp
namespace Orca.Core.Run;

/// <summary>Observer of one human-triggered run (checkpoint attempt or planning run).
/// Same threading contract as IClaudeStreamObserver: sequential per run, background
/// thread, must not block or throw.</summary>
public interface IRunObserver
{
    void OnRunEvent(RunObservation observation);
}

/// <summary>Closed union of everything a run surfaces while in flight. Payloads reuse
/// the existing evidence types — nothing here is a second source of truth.</summary>
public abstract record RunObservation
{
    private RunObservation() { }

    /// <summary>One claude invocation is starting. Invocation numbers §5.4 parse
    /// retries within the attempt (1-based), matching the run-log file names.</summary>
    public sealed record RoleStarted(RoleName Role, int Attempt, int Invocation,
        string EventLogPath) : RunObservation;

    public sealed record RoleStreamEvent(RoleName Role, ClaudeStreamEvent Event) : RunObservation;

    public sealed record RoleFinished(RoleName Role, ClaudeExecutionResult Execution) : RunObservation;

    /// <summary>A §5.4 parse/validation failure entered the capped Reflexion retry.</summary>
    public sealed record RoleParseRetry(RoleName Role, string Error, int NextInvocation) : RunObservation;

    public sealed record GateStarted(string Gate, IReadOnlyList<string> Command) : RunObservation;

    public sealed record GateFinished(GateCommandResult Result) : RunObservation;

    /// <summary>Checkpoint branch created or switched to (Implementer runs only).</summary>
    public sealed record BranchReady(string Branch, bool Created) : RunObservation;

    /// <summary>A canonical vault record landed ("reconciliation"/"verification"/"plan").</summary>
    public sealed record VaultRecordWritten(string Kind, string Path) : RunObservation;
}
```

Threaded as an optional parameter through the three mutating entry points:

```csharp
CheckpointRunner .RunAsync(preview, IRunObserver? observer = null, ct = default)
CheckpointVerifier.RunAsync(plan, checkpoint, attempt, IRunObserver? observer = null, ct = default)
IPlanningStrategy.GenerateAsync(preview, IRunObserver? observer = null, ct = default)   // interface change
```

The CLI passes `null` and keeps today's output byte-for-byte; the TUI passes its adapter.
This is what makes the live pane cover **all four roles**: Planner via the strategy,
Implementer via the runner, Reconciler/Verifier via the verifier's role loops.

### 3.3 The human gate, reified — `ICheckpointGate` + `CheckpointRunSession`

The structural decision of this feature. The flow currently inlined in
`CheckpointRunCommand.ExecuteAsync` (gitignore guard → pending-decision resume → run →
failure branching → verification → decision application) becomes one Core service, and
the three points where it consults the human become one interface. The front ends render
and ask; the sequence itself can no longer fork between them.

```csharp
namespace Orca.Core.Run;

public enum CheckpointChoice { Approve, Reject, Defer }

public sealed record CheckpointDecision
{
    public required CheckpointChoice Choice { get; init; }

    /// <summary>Reject only: the human explicitly confirmed the hard reset
    /// (spec §7.1 — offered, never automatic).</summary>
    public bool DiscardChanges { get; init; }

    /// <summary>True when a non-interactive front end defaulted to Defer because no
    /// human could answer (§3.6 exit-code mapping). Never set by a real choice.</summary>
    public bool AutoDeferred { get; init; }
}

/// <summary>Everything the decision moment can render: the preview, the run outcome (null
/// when resuming a previously deferred attempt), verification, working-tree evidence,
/// and this attempt's cost.</summary>
public sealed record CheckpointDecisionContext
{
    public required CheckpointRunPreview Preview { get; init; }
    public required CheckpointRunOutcome? Run { get; init; }
    public required CheckpointVerificationOutcome? Verification { get; init; }
    public required IReadOnlyList<string> StatusLines { get; init; }
    public required string DiffStat { get; init; }
    public required decimal? AttemptCostUsd { get; init; }
}

/// <summary>The per-checkpoint human gate (spec §7.1), as an interface both front ends
/// implement. Core calls it; nothing in Core ever answers it.</summary>
public interface ICheckpointGate
{
    /// <summary>A failed invocation left partial changes: offer discarding them.</summary>
    Task<bool> ConfirmDiscardAfterFailureAsync(CheckpointDecisionContext context, CancellationToken ct);

    /// <summary>A resumed pending attempt has no verification record: offer running
    /// gates + Reconciler + Verifier now (costs quota).</summary>
    Task<bool> ConfirmVerifyPendingAsync(int pendingAttempt, CancellationToken ct);

    /// <summary>The approve/reject/defer gate itself.</summary>
    Task<CheckpointDecision> DecideAsync(CheckpointDecisionContext context, CancellationToken ct);
}

/// <summary>One human-triggered checkpoint session, start to decision. Owns the exact
/// sequence CheckpointRunCommand owns today; both front ends become thin callers.</summary>
public sealed class CheckpointRunSession(OrcaConfig config, GitWorkspace workspace, IClaudeExecutor executor)
{
    /// <summary>Read-only; delegates to CheckpointRunner.Prepare. Front ends render the
    /// preview (and --dry-run stops here, exactly as today).</summary>
    public CheckpointRunPreview Prepare(string? planId, string? checkpointId);

    public Task<CheckpointSessionResult> ExecuteAsync(
        CheckpointRunPreview preview,
        ICheckpointGate gate,
        IRunObserver? observer = null,
        CancellationToken cancellationToken = default);
}

public enum CheckpointSessionOutcome
{
    GitignoreAdded,          // stopped before running: commit the .gitignore change, re-run
    InvocationFailed,        // implementer stop reason != succeeded; gate already consulted about partial changes
    NoChanges,               // invocation succeeded but tree is clean — a failed attempt
    VerificationIncomplete,  // pipeline stopped before a record (auth/rate-limit/budget/timeout/parse ceiling)
    Approved,                // committed; CommitSha is set
    Rejected,                // DiscardedChanges says whether the confirmed reset ran
    Deferred,                // human deferred, or AutoDeferred on a non-interactive terminal
    Interrupted,             // cancellation killed the invocation; run state resumes it, nothing re-paid
}

public sealed record CheckpointSessionResult
{
    public required CheckpointSessionOutcome Outcome { get; init; }
    public required CheckpointRunPreview Preview { get; init; }
    public CheckpointRunOutcome? Run { get; init; }
    public CheckpointVerificationOutcome? Verification { get; init; }
    public string? CommitSha { get; init; }
    public bool DiscardedChanges { get; init; }
    public bool AutoDeferred { get; init; }
    public decimal? AttemptCostUsd { get; init; }
}
```

Notes:

- `CheckpointRunner` and `CheckpointVerifier` keep their exact public shapes (plus the
  observer parameter); the session composes them. Approve/Reject stay separate runner
  methods the session calls after the gate answers — the gate can never mutate anything
  itself.
- The `.orca/` gitignore guard moves into the session (it is a workflow rule, not
  presentation); its "commit that and re-run" stop becomes the typed
  `GitignoreAdded` outcome both front ends render their own way.
- Cancellation maps to `Interrupted`: the executor already kills the process tree and
  the existing resume machinery (records + run state) already recovers interrupted
  attempts without re-paying — the TUI's Interrupt button is the Ctrl-C the CLI always
  had, made explicit and recorded.
- `plan-generate` needs no gate interface (its only human decision — single vs multi —
  happens before anything runs, and parse retries are already automatic by owner
  decision). It needs only the `GenerateAsync` observer parameter plus the same
  gitignore-guard/refusal handling, which stays in its two thin front ends.

### 3.4 Status as a query — `StatusReport`

The data `StatusCommand` assembles inline becomes a Core query both front ends share —
it is the TUI tree's entire backing model:

```csharp
namespace Orca.Core.Run;

public static class StatusReportBuilder
{
    /// <summary>planId null = all plans in the vault (the TUI tree); an explicit id or a
    /// single-plan vault narrows to one (today's StatusCommand behavior).</summary>
    public static StatusReport Build(OrcaConfig config, GitWorkspace workspace, string? planId);
}

public sealed record StatusReport
{
    public required string RepoRoot { get; init; }
    public required string CurrentBranch { get; init; }
    public required bool WorkingTreeClean { get; init; }
    public required string VaultRoot { get; init; }
    public required string PlansDir { get; init; }
    public required IReadOnlyList<PlanStatusEntry> Plans { get; init; }
}

public sealed record PlanStatusEntry
{
    public required Plan Plan { get; init; }
    public required RunState? RunState { get; init; }
    /// <summary>Checkpoint id → latest vault evidence (either record may be absent).</summary>
    public required IReadOnlyDictionary<string, CheckpointEvidence> Evidence { get; init; }
}

public sealed record CheckpointEvidence
{
    public required VerificationRecord? LatestVerification { get; init; }
    public required ReconciliationRecord? LatestReconciliation { get; init; }
}
```

Supporting change: `PlanLocator` gains `ListPlanIds(VaultLayout)` (the enumeration that
already lives inside `ResolvePlanId`, made public). Resolution semantics for the
subcommands do not change.

Refresh policy: **no file watcher.** The report is rebuilt on demand — when the TUI
opens, after every session, and on an explicit refresh key. A `FileSystemWatcher` over an
Obsidian vault (sync churn, two machines, cross-platform quirks) is exactly the kind of
ambient magic this tool avoids; if it ever earns its way in, that is its own proposal.

### 3.5 Display projection — `StreamEventDisplay`

The live pane's content. `ClaudeStreamEvent` is deliberately shallow — the strict
contract parses only what Orca *acts on* (init/result/error/rate-limit), and assistant
text and tool calls exist only in `RawLine`. The pane needs those, and they must **not**
be added to the strict contract (that would grow the fail-loud surface and the billed
contract test for presentation's sake). Instead, a lenient, read-only projection:

```csharp
namespace Orca.Core.Execution;

/// <summary>Lenient projection of one parsed stream event into human-readable lines for
/// live rendering. Never throws, never gates anything: unknown or drifted shapes come
/// back as the bare event type. The strict §6 contract remains ClaudeStreamEvent's.</summary>
public static class StreamEventDisplay
{
    public static IReadOnlyList<DisplayLine> Describe(ClaudeStreamEvent streamEvent);
}

public enum DisplayKind { Init, AssistantText, ToolUse, ToolResult, Result, RateLimit, Other }

public sealed record DisplayLine
{
    public required DisplayKind Kind { get; init; }
    /// <summary>e.g. "Read(src/Foo.cs)" · first line of assistant text · "$1.14 · 23 turns · 4m10s"</summary>
    public required string Text { get; init; }
}
```

One event may yield several lines (an assistant message can carry text plus several
`tool_use` blocks). It lives in Core because it is a pure projection with no UI types,
unit-tested against the committed transcript fixtures — and it incidentally enables a
future CLI `--follow` mode for free (a non-goal now, §7).

### 3.6 Entry point and project layout

**No new project.** Spec §2's rule stands: extract a project when a genuine second
consumer emerges, not preemptively. The TUI lives in `Orca.Cli` under a `Tui/` folder;
`Terminal.Gui` is added to `Orca.Cli` only. `Orca.Core` stays UI-free (neither Spectre
nor Terminal.Gui), which is what actually guards the "thin shell over tested Core"
principle — project count is incidental.

```
src/Orca.Cli/
  Program.cs               # bare-`orca` branch + Spectre dispatch (unchanged commands)
  Commands/                # the Spectre lane, byte-for-byte behavior preserved
  Tui/
    TuiEntry.cs            # TTY detection, Application lifecycle, refuse-cleanly path
    MainScreen.cs          # tree + detail + activity + status bar
    RunEventChannel.cs     # IRunObserver adapter: bounded channel → Application.Invoke
    TuiCheckpointGate.cs   # ICheckpointGate as modal dialogs
    ...views
```

Entry rules:

- `orca` (no args) on an interactive terminal → TUI. No args on a **non**-interactive
  terminal → today's Spectre help text, unchanged (scripts that call bare `orca` see no
  difference).
- `orca ui [--repo <path>]` → explicit TUI launch (flags, muscle memory, shells that
  misreport). On a non-interactive terminal it refuses cleanly: one line naming the
  subcommands, exit 1 — never a Terminal.Gui init crash.
- Interactivity check: `Console.IsInputRedirected || Console.IsOutputRedirected` (plus
  `TERM=dumb` on Unix) checked *before* `Application.Init`, not caught after.

**The non-TTY fix for the subcommands** (the README known limitation) falls out of §3.3
and lands before any TUI code: the Spectre `ICheckpointGate` detects a non-interactive
console and, instead of crashing at the prompt after quota was spent, returns
`Defer + AutoDeferred` — the run lands in exactly the deferred state the resume machinery
already handles free. `checkpoint-run` exits **2** for auto-defer ("ran, needs a human")
vs 0 for a human's deliberate defer. `plan-generate` on a non-TTY without `--strategy`
refuses up front (deliberation depth is a human decision and is never defaulted — spec
§8), rather than crashing at the selection prompt.

## 4. The TUI itself

```
┌─ Orca ────────────────────────────────────────────────────────────────────┐
│ plans (vault)          │ detail: 03-verifier-loop            [checkpoint] │
│  ▾ orca-build          │ description, acceptance criteria,               │
│    ✓ 01-scaffold       │ latest verification + reconciliation verdicts,  │
│    ✓ 02-domain-model   │ attempt count, branch                           │
│    → 03-verifier-loop  ├─────────────────────────────────────────────────┤
│    · 04-e2e            │ activity                                        │
│  ▸ other-plan          │ [implementer] ⚒ Read(src/Api/Health.cs)         │
│                        │ [implementer] ✎ "Adding the endpoint …"         │
│                        │ [gates] build: pass (3.2s)                      │
│                        │ [verifier] ⚒ Write(.staging/…)                  │
├────────────────────────┴─────────────────────────────────────────────────┤
│ repo RecipeApp · branch orca/... (dirty) · vault my-vault · spent $3.41  │
└───────────────────────────────────────────────────────────────────────────┘
```

- **Left: tree.** Plans → checkpoints, status-colored with the same mapping
  `StatusCommand` uses today. Node model is a typed recursive record from day one so the
  planned roadmap→epic→plan hierarchy (separate proposal, out of scope here) adds a
  level without redesign — the tree renders node depth, it does not hardcode two levels.
- **Right top: detail** of the selection, straight from `StatusReport` (plan frontmatter
  facts, or checkpoint description + acceptance criteria + latest evidence).
- **Right bottom: activity.** Idle: last run's summary facts from run state. Live: the
  role-tagged event stream.
- **Status bar:** repo, current branch + dirty flag, vault, accumulated cost from run
  state (updated per `RoleFinished`).
- **Actions** are explicit and few: run selected checkpoint (opens the preview modal —
  the authorize-the-spend moment — then the live view), resume pending decision,
  generate plan (step 7), refresh, quit. Quit is blocked while a session is live except
  via Interrupt first.
- **Modals:** the three `ICheckpointGate` calls, each rendering its
  `CheckpointDecisionContext` (verdicts, diffstat, cost) before asking. Approve/Reject/
  Defer are buttons; Reject chains the discard confirmation exactly as today.

**Threading model.** The session runs on a background `Task`. The `IRunObserver` adapter
writes observations into a bounded channel; a drain marshals batches onto the UI thread
via `Application.Invoke`, coalescing repaints (at most one layout pass per batch, batches
at UI cadence, not event cadence). Executor threads never touch widgets; the UI thread
never blocks on the stream. Backpressure policy: the channel is bounded and, if the UI
ever falls behind, drops *display* events oldest-first while counting drops visibly in
the pane header — the run log on disk remains the complete record either way, so the
pane can honestly say "display elided N events; the log has everything."

**Testability.** Everything below the shell (observer plumbing, session sequencing, gate
protocol, status query, display projection) is plain Core code, unit-tested with the
existing fake-executor patterns — including a scripted `ICheckpointGate` fake driving
every branch of the session, which puts the currently-untestable command flow under test
for the first time. The Terminal.Gui shell itself is verified manually on both
development machines (§6 step 8); if v2's headless driver proves workable during step 4,
a smoke test (launch → tree renders → quit) may be added, but nothing in this plan
depends on it.

## 5. Invariants, restated as mechanisms

| Rule | Enforced by |
| --- | --- |
| One invocation per checkpoint attempt | unchanged — `CheckpointRunner`; the session adds no loop; the TUI has no run-all |
| No auto-advance past a gate | `ExecuteAsync` ends after one decision; next action requires a fresh human trigger |
| Live pane is read-only | observer interfaces have no control methods; the one control (Interrupt) is cancellation, recorded, resumable |
| No background work between screens | one modal session at a time; session end = workflow stopped |
| Nothing bypasses the gates on a non-TTY | auto-defer via the same `ICheckpointGate`, exit 2 |
| Vault stays durable-knowledge-only | TUI writes nothing to the vault; approval of draft plans remains a human edit in Obsidian (out of scope, §7) |
| Evidence is never forked | observer/display layers are projections; run log + vault records remain the only sources of truth |

## 6. Build order

One step per session, owner review between steps, same protocol as the original build.
Steps 1–3 contain zero Terminal.Gui code; the riskiest new ground (the framework) is
touched only after the contracts beneath it are proven.

1. **Executor observer + display projection** (§3.1, §3.5). Update call sites and test
   fakes; fixture-driven tests for `StreamEventDisplay` over the committed transcripts.
   No behavior change anywhere.
2. **Progress + session extraction** (§3.2, §3.3, §3.4). Thread `IRunObserver` through
   runner/verifier/strategy; build `CheckpointRunSession` + `ICheckpointGate` +
   `StatusReport`; rewire both commands as thin callers with a Spectre gate. Behavior-
   preserving: existing tests green, CLI output unchanged, plus new session tests with
   scripted gates.
3. **Non-TTY fix** (§3.6). Auto-defer gate + exit 2; plan-generate strategy refusal.
   Small, independently verifiable, retires the README known limitation before any UI
   work.
4. **TUI entry + read-only shell.** Bare-`orca` branch, `orca ui`, TTY refusal,
   tree + detail + status bar over `StatusReport`. No actions yet beyond navigate/
   refresh/quit.
5. **Run a checkpoint from the TUI.** Preview modal → session on background task →
   `TuiCheckpointGate` dialogs. Activity pane shows lifecycle events only (roles, gates,
   records) — proving the threading model on low event volume first.
6. **Full live pane.** Stream events through `StreamEventDisplay` for all four roles;
   scrollback; drop-counting; Interrupt affordance.
7. **Plan-generate screen.** Form (id, goal text/file, strategy — multi still refuses),
   live Planner pane via the same infrastructure.
8. **Cross-platform validation + docs.** Live pane exercised on Arch/Hyprland terminal
   and Windows Terminal (both machines); README: pinned-tools row (Terminal.Gui 2.4.17),
   commands section (`ui`), remove the retired known limitation; sync note to the vault.

Step 2 is the largest; if it strains a session, split at the natural seam: 2a observer
threading (runner/verifier/strategy), 2b session + gate + status extraction.

## 7. Non-goals for this feature

- No roadmap/epic hierarchy and no model tiering (separate pending proposal; only the
  tree's node model here is shaped so that proposal won't force a redesign).
- No multi-candidate planning UI (`multi` keeps refusing exactly as today).
- No plan-file editing from the TUI — approving a draft stays a deliberate human act in
  the vault. (Revisit only as its own proposal; it changes who writes plan files.)
- No CLI `--follow`/watch mode, no vault file watcher, no TUI config-editing screens.
- No parallel sessions, no queueing of checkpoints, no reuse of a TUI session across
  repos (one repo per launch, like every subcommand).
- No Spectre → Terminal.Gui migration of the subcommands; the CLI lane's output is
  frozen behavior.

## 8. Owner decisions pending — confirm or override before step 1

1. **Terminal.Gui 2.4.17 pin** as a new `Orca.Cli`-only dependency, recorded in the
   README pinned-tools table (§1). Default: as written.
2. **TUI inside `Orca.Cli`**, no new project (§3.6). Default: as written.
3. **Entry semantics:** bare `orca` on a TTY opens the TUI; non-TTY bare `orca` keeps
   printing help; explicit subcommand named `ui`. Default: as written; alternative name
   (`tui`) if preferred.
4. **`ICheckpointGate` + `CheckpointRunSession` shapes** (§3.3) — the structural
   approval; everything in steps 2–7 depends on these two.
5. **`IClaudeExecutor.ExecuteAsync` signature change** (§3.1) and
   **`IPlanningStrategy.GenerateAsync` observer parameter** (§3.2) — both are approved-
   contract amendments, flagged per the working agreement.
6. **Non-TTY auto-defer exits 2** (vs today's crash; a human's deliberate defer stays 0)
   and **plan-generate refuses on non-TTY without `--strategy`** (§3.6).
7. **Interrupt affordance** in the live pane (cancellation-kill, recorded, resumable —
   the explicit Ctrl-C). Default: include in step 6.
8. **This document's home:** repo `docs/` now; owner syncs to the vault (as with
   kickoff-v2) whenever it reaches approved status.

### Resolution — 2026-07-09 (owner approval, recorded by Claude Code)

All eight decisions resolved with the owner in the vault-side review session; the plan
is **approved** and step 1 of §6 is unblocked.

1. **Approved as written** — Terminal.Gui pinned at 2.4.17, `Orca.Cli`-only, README
   pinned-tools row. Re-verify "2.4.17 is current stable and ships
   `Application.Invoke`" against NuGet at step 4, when the package is actually added
   (the 2026-07-09 check predates implementation).
2. **Approved as written** — TUI lives in `Orca.Cli` under `Tui/`; no new project.
3. **Approved as written** — bare `orca` on a TTY opens the TUI; non-TTY bare `orca`
   keeps printing help; the explicit subcommand is named `ui` (not `tui`).
4. **Approved as written** — `ICheckpointGate` + `CheckpointRunSession` shapes (§3.3).
5. **Approved as written** — the breaking `IClaudeExecutor.ExecuteAsync` observer
   parameter (§3.1) and the `IPlanningStrategy.GenerateAsync` observer parameter
   (§3.2), both as approved-contract amendments.
6. **Approved as written, plus a gap the plan left open, now decided:** on a
   non-interactive terminal the Spectre `ICheckpointGate` **declines both**
   confirmations — `ConfirmDiscardAfterFailureAsync` returns false (partial changes are
   never discarded without a human) and `ConfirmVerifyPendingAsync` returns false (no
   quota is spent unattended); everything lands in the deferred/resumable state.
   Auto-defer exits 2; a human's deliberate defer stays 0; `plan-generate` on a non-TTY
   without `--strategy` refuses up front.
7. **Approved as written** — Interrupt affordance ships in step 6.
8. **Approved as written** — this document stays in repo `docs/`; the owner syncs to
   the vault at approved status (a copy already lives in the vault's
   `Suggestions/Alp-Notes.md`, updated with this same resolution).

Also settled during review: the status bar's "spent $" figure is the **selected plan's**
accumulated cost from run state (not a vault-wide total), matching run state's
granularity. `ConfirmVerifyPendingAsync` deliberately receives only the attempt number —
minimal context for a yes/no quota decision — confirmed as intended, not an omission.
