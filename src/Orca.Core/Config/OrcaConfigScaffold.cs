using System.Text.Json;

namespace Orca.Core.Config;

/// <summary>Produces a committed target-repo <c>orca.json</c> from the machine's resolved
/// vault name and a best-effort read of the repo — the <c>orca init</c> mechanics, modeled
/// on <see cref="Orca.Core.Vault.PlanScaffold"/>. The generated text is round-tripped through
/// <see cref="ConfigDocumentParser"/> before it is returned, so the scaffold can never emit a
/// file the schema refuses; a full <see cref="ConfigLoader.Load"/> round-trip (which also
/// resolves the vault) is the acceptance gate in the tests. The scaffold never writes machine
/// paths: it names the vault via <c>vault.name</c> only, exactly as a committed file must
/// (<see cref="ConfigLoader"/> rejects a repo file that carries a <c>vaults</c> block).</summary>
public static class OrcaConfigScaffold
{
    /// <summary>The argv token written for <c>commands.build</c>/<c>commands.test</c> when no
    /// build system is detected: it loads (non-empty, non-blank) but fails the gate loud on the
    /// first run until a human edits it — scaffold-then-review, not a silent guess.</summary>
    public const string PlaceholderToken = "REPLACE_ME";

    /// <summary>Builds the human-readable (2-space indented) JSON for a fresh repo's
    /// <c>orca.json</c>. Pure — the only I/O in this type is
    /// <see cref="DetectCommands"/> reading the repo directory.</summary>
    public static string Build(OrcaConfigScaffoldInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.VaultName))
            throw new ArgumentException("vault name must be non-empty", nameof(input));
        if (string.IsNullOrWhiteSpace(input.RepoName))
            throw new ArgumentException("repo name must be non-empty", nameof(input));
        ArgumentNullException.ThrowIfNull(input.Commands);

        var plansDir = string.IsNullOrWhiteSpace(input.PlansDir)
            ? $"{input.RepoName}/Plans"
            : input.PlansDir;

        // An ordered object graph rather than a hand-built string: System.Text.Json guarantees
        // valid JSON and correct escaping, and the field order below is declaration order. The
        // four-role block is the README's proven minimal shape, copied verbatim (a human reviews
        // and adapts the tool-specific pieces — e.g. the Implementer's dotnet Bash prefixes — before
        // committing). No comments: ConfigDocumentParser parses strict JSON and would reject them.
        var document = new
        {
            schemaVersion = 1,
            vault = new
            {
                name = input.VaultName,
                plansDir,
            },
            commands = new
            {
                build = input.Commands.Build,
                test = input.Commands.Test,
            },
            gateTimeoutSeconds = 600,
            maxParseRetries = 2,
            maxCheckpointAttempts = 3,
            // Opinionated model tiering by default (SUG-05): the manual pattern this
            // scaffold is meant to replicate is "higher model plans, Sonnet/Haiku
            // implements" — without it every role silently runs the CLI's own default and
            // the tiering only happens if a human remembers to configure it. A configured
            // model your plan doesn't offer fails loud at invocation time (README note),
            // never here.
            roles = new
            {
                planner = new
                {
                    allowedTools = new[] { "Read", "Write", "Edit" },
                    maxTurns = 30,
                    timeoutSeconds = 900,
                    model = "opus",
                    maxBudgetUsd = 2,
                },
                implementer = new
                {
                    allowedTools = new[]
                    {
                        "Read", "Glob", "Grep", "Write", "Edit",
                        "Bash(dotnet build*)", "Bash(dotnet test*)",
                        "Bash(git status*)", "Bash(git diff*)", "Bash(git log*)",
                    },
                    maxTurns = 60,
                    timeoutSeconds = 1800,
                    model = "sonnet",
                    maxBudgetUsd = 5,
                },
                reconciler = new
                {
                    allowedTools = new[] { "Read", "Write", "Edit" },
                    maxTurns = 15,
                    timeoutSeconds = 600,
                    model = "haiku",
                    maxBudgetUsd = 2,
                },
                verifier = new
                {
                    allowedTools = new[] { "Read", "Write", "Edit" },
                    maxTurns = 15,
                    timeoutSeconds = 600,
                    model = "sonnet",
                    maxBudgetUsd = 2,
                },
                // SUG-08: the goal-refinement interviewer behind `orca goal` — one cheap
                // vault-scoped invocation that sharpens a raw goal into a reviewable
                // brief before the Planner spends anything.
                interviewer = new
                {
                    allowedTools = new[] { "Read", "Write", "Edit" },
                    maxTurns = 15,
                    timeoutSeconds = 600,
                    model = "sonnet",
                    maxBudgetUsd = 1,
                },
            },
        };

        var json = JsonSerializer.Serialize(document, SerializeOptions).ReplaceLineEndings("\n") + "\n";

        // Fail loud before anyone tries to load it if the scaffold and the schema ever drift apart —
        // the same parse-before-write guard PlanScaffold uses. The tests take it further and run the
        // full ConfigLoader.Load, which also resolves the vault.
        ConfigDocumentParser.Parse(json, "orca.json (scaffold self-check)");

        return json;
    }

    /// <summary>Best-effort detection of build/test commands from the repo root — a convenience
    /// the caller is expected to <em>echo</em>, never a silent guess. Reads only the top-level
    /// directory listing. Falls back to a <see cref="PlaceholderToken"/> pair the human must edit.</summary>
    public static DetectedCommands DetectCommands(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new ArgumentException("repo root must be a non-empty path", nameof(repoRoot));

        // Order matters: a solution file describes the whole build; a lone project is the next best
        // thing; package.json is the JS fallback. "Exactly one" throughout — an ambiguous match
        // (two .slnx, several .csproj) falls through rather than picking one arbitrarily.
        if (SingleWithExtension(repoRoot, ".slnx") is { } slnx)
            return Dotnet(slnx);
        if (SingleWithExtension(repoRoot, ".sln") is { } sln)
            return Dotnet(sln);
        if (SingleWithExtension(repoRoot, ".csproj") is { } csproj)
            return Dotnet(csproj);

        if (File.Exists(Path.Combine(repoRoot, "package.json")))
            return new DetectedCommands
            {
                Build = ["npm", "run", "build"],
                Test = ["npm", "test"],
                Summary = "detected package.json → npm run build / npm test",
                IsPlaceholder = false,
            };

        return new DetectedCommands
        {
            Build = [PlaceholderToken],
            Test = [PlaceholderToken],
            Summary =
                $"no build system detected — commands.build/test set to \"{PlaceholderToken}\"; edit them before running",
            IsPlaceholder = true,
        };
    }

    /// <summary>Resolves <c>vault.plansDir</c> to its one canonical location,
    /// <c>Orca/&lt;repoName&gt;/Plans</c>, and — when the vault root exists on this machine — creates the
    /// <c>Orca/</c>, <c>Orca/&lt;repoName&gt;/</c>, and <c>Orca/&lt;repoName&gt;/Plans</c> folders under it
    /// (owner decision 2026-07-15: one fixed path, never a walk of the vault for a folder that happens to share
    /// the repo's name). This is the only method here that writes to disk, and it keeps <see cref="DetectPlansDir"/>'s
    /// fail-never posture: a vault root that is absent on this machine creates nothing (mirroring the old fallback
    /// trigger), and an <see cref="UnauthorizedAccessException"/>/<see cref="IOException"/> creating the folder
    /// degrades to returning the path string with nothing created rather than crashing <c>orca init</c>. Creation is
    /// idempotent: <see cref="Directory.CreateDirectory(string)"/> is a no-op when the folder is already there. The
    /// caller only reaches this when no <c>--plans-dir</c> was given; an explicit flag is trusted and written
    /// straight through, so nothing is ever created when the human named their own location. plansDir is always
    /// spelled with forward slashes — <see cref="Orca.Core.Vault.VaultLayout"/> re-splits it on them.</summary>
    public static PreparedPlansDir DetectPlansDir(string vaultRoot, string repoName)
    {
        if (string.IsNullOrWhiteSpace(repoName))
            throw new ArgumentException("repo name must be non-empty", nameof(repoName));

        var plansDir = $"Orca/{repoName}/Plans";

        if (string.IsNullOrWhiteSpace(vaultRoot) || !Directory.Exists(vaultRoot))
            return new PreparedPlansDir
            {
                PlansDir = plansDir,
                Summary =
                    $"vault root not found on this machine — plansDir \"{plansDir}\"; its folders will be created in the vault on first use",
            };

        try
        {
            // One call creates Orca/, Orca/<repoName>/, and Orca/<repoName>/Plans/ (CreateDirectory makes every
            // missing intermediate) and no-ops when they already exist.
            Directory.CreateDirectory(Path.Combine(vaultRoot, "Orca", repoName, "Plans"));
            return new PreparedPlansDir
            {
                PlansDir = plansDir,
                Summary = $"plansDir \"{plansDir}\" — folder ready in the vault",
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new PreparedPlansDir
            {
                PlansDir = plansDir,
                Summary =
                    $"plansDir \"{plansDir}\" — could not create its folder in the vault ({ex.Message}); it will be created on first use",
            };
        }
    }

    private static DetectedCommands Dotnet(string projectPath)
    {
        var file = Path.GetFileName(projectPath);
        return new DetectedCommands
        {
            Build = ["dotnet", "build", file],
            Test = ["dotnet", "test", file],
            Summary = $"detected {file} → dotnet build/test {file}",
            IsPlaceholder = false,
        };
    }

    /// <summary>The single top-level file with this extension, or null when there are zero or
    /// several. Filters on <see cref="Path.GetExtension(string)"/> rather than a
    /// <c>*.ext</c> search pattern on purpose: on Windows a 3-char pattern extension like
    /// <c>*.sln</c> also matches <c>*.slnx</c>, which would misclassify a solution repo.</summary>
    private static string? SingleWithExtension(string repoRoot, string extension)
    {
        var matches = Directory.EnumerateFiles(repoRoot)
            .Where(f => Path.GetExtension(f).Equals(extension, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static readonly JsonSerializerOptions SerializeOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            IndentSize = 2,
        };
}

/// <summary>Everything the scaffold needs that it cannot read from the repo: the resolved vault
/// name (which must already be a key in the machine's global <c>vaults</c> map) and the detected
/// commands. <see cref="PlansDir"/> is optional and defaults to <c>&lt;RepoName&gt;/Plans</c>.</summary>
public sealed record OrcaConfigScaffoldInput
{
    public required string VaultName { get; init; }
    public required string RepoName { get; init; }
    public string? PlansDir { get; init; }
    public required DetectedCommands Commands { get; init; }
}

/// <summary>The build/test argv pair chosen by <see cref="OrcaConfigScaffold.DetectCommands"/>,
/// with a human-readable <see cref="Summary"/> the CLI echoes and an <see cref="IsPlaceholder"/>
/// flag distinguishing a real detection from the edit-me fallback.</summary>
public sealed record DetectedCommands
{
    public required IReadOnlyList<string> Build { get; init; }
    public required IReadOnlyList<string> Test { get; init; }
    public required string Summary { get; init; }
    public required bool IsPlaceholder { get; init; }
}

/// <summary>The canonical <c>vault.plansDir</c> resolved by
/// <see cref="OrcaConfigScaffold.DetectPlansDir"/> and the human-readable <see cref="Summary"/> the
/// CLI echoes. There is only ever one path now — <c>Orca/&lt;repoName&gt;/Plans</c> — so, unlike the
/// old vault scan, there is nothing to pick and no fallback to flag.</summary>
public sealed record PreparedPlansDir
{
    public required string PlansDir { get; init; }
    public required string Summary { get; init; }
}
