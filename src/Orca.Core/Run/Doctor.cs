using System.Diagnostics;
using System.Text.RegularExpressions;
using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Vault;

namespace Orca.Core.Run;

public enum DoctorLevel
{
    Pass,
    Info,
    Warn,
    Fail,
}

/// <summary>One doctor check's outcome: section, verdict, what was found, and the one
/// action line that fixes it (null when nothing to do).</summary>
public sealed record DoctorFinding(string Section, DoctorLevel Level, string Message, string? Action = null);

/// <summary>SUG-10: <c>orca doctor</c> — zero-cost, deterministic preflight and drift
/// detection. Everything here is checkable without spending a token: environment
/// binaries and the CLI version pin, config/vault integrity (sync-conflict artifacts,
/// unparseable plans, status-vs-evidence drift), and repo↔vault drift (missing branches
/// from another machine, unmerged approvals, attempt-number divergence). The logged-in
/// check is deliberately absent per spec §4 — auth state is unknowable until the first
/// invocation, and doctor says so instead of guessing.</summary>
public sealed class Doctor(
    OrcaConfig config,
    GitWorkspace workspace,
    Func<string, string[], string?>? probe = null)
{
    private readonly Func<string, string[], string?> _probe = probe ?? RunProbe;
    private readonly VaultLayout _layout = new(config.VaultRoot, config.PlansDir);

    public IReadOnlyList<DoctorFinding> Run()
    {
        var findings = new List<DoctorFinding>();
        CheckEnvironment(findings);
        CheckVault(findings, out var plans);
        CheckRepoDrift(findings, plans);
        return findings;
    }

    public static bool HasFailures(IReadOnlyList<DoctorFinding> findings) =>
        findings.Any(f => f.Level == DoctorLevel.Fail);

    private void CheckEnvironment(List<DoctorFinding> findings)
    {
        const string section = "environment";

        var claudeVersion = _probe("claude", ["--version"]);
        if (claudeVersion is null)
        {
            findings.Add(new(section, DoctorLevel.Fail, "claude is not on PATH",
                "install Claude Code CLI and log in interactively once"));
        }
        else
        {
            var installed = claudeVersion.Trim().Split(' ')[0];
            findings.Add(installed == ClaudeVersionCheck.PinnedVersion
                ? new(section, DoctorLevel.Pass, $"claude {installed} matches the pinned {ClaudeVersionCheck.PinnedVersion}")
                : new(section, DoctorLevel.Warn,
                    $"claude {installed} installed, but Orca's stream-json contract is pinned against {ClaudeVersionCheck.PinnedVersion}",
                    "run the billed contract test (dotnet test --settings tests/integration.runsettings) before trusting this version, then bump the pin"));
        }

        findings.Add(_probe("git", ["--version"]) is { } gitVersion
            ? new(section, DoctorLevel.Pass, gitVersion.Trim())
            : new(section, DoctorLevel.Fail, "git is not on PATH", "install git"));

        CheckGateBinary(findings, "commands.build", config.BuildCommand);
        CheckGateBinary(findings, "commands.test", config.TestCommand);

        findings.Add(new(section, DoctorLevel.Info,
            "auth state unknown until the first invocation (by design, spec §4) — a logged-out machine fails loudly at invocation time"));
    }

    private void CheckGateBinary(List<DoctorFinding> findings, string name, IReadOnlyList<string> command)
    {
        const string section = "environment";
        var binary = command.Count > 0 ? command[0] : "";
        if (binary.Length == 0 || binary == "REPLACE_ME")
        {
            findings.Add(new(section, DoctorLevel.Fail, $"{name} is not configured ('{binary}')",
                $"edit {name} in {config.RepoConfigPath}"));
            return;
        }

        findings.Add(ResolvesOnPath(binary)
            ? new(section, DoctorLevel.Pass, $"{name} binary '{binary}' resolves on PATH")
            : new(section, DoctorLevel.Fail, $"{name} binary '{binary}' does not resolve on PATH",
                "install it or fix the command — the gate would crash mid-pipeline otherwise"));
    }

    private void CheckVault(List<DoctorFinding> findings, out List<Plan> plans)
    {
        const string section = "config & vault";
        plans = [];

        if (config.PlansDir.Contains('\\'))
            findings.Add(new(section, DoctorLevel.Fail,
                $"plansDir '{config.PlansDir}' contains backslashes — it breaks on the other machine (BF-09)",
                "spell it with forward slashes in the config"));

        if (!Directory.Exists(_layout.PlansRoot))
        {
            findings.Add(new(section, DoctorLevel.Warn, $"plans directory does not exist yet: {_layout.PlansRoot}",
                "orca plan-init creates it"));
            return;
        }

        findings.Add(new(section, DoctorLevel.Pass, $"vault '{config.VaultName}' resolved, plans directory exists"));

        // Sync-conflict artifacts brick a plan until hand-deleted — find them first.
        foreach (var file in Directory.EnumerateFiles(_layout.PlansRoot, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Contains(".sync-conflict-", StringComparison.Ordinal)
                || DuplicateSuffix.IsMatch(fileName))
                findings.Add(new(section, DoctorLevel.Fail, $"sync artifact in the plans tree: {file}",
                    "resolve the conflict and delete the artifact — parsers fail loud on these"));
        }

        foreach (var planId in PlanLocator.ListPlanIds(_layout))
        {
            try
            {
                var plan = new VaultReader(_layout).ReadPlan(planId);
                plans.Add(plan);

                if (plan.Status == PlanStatus.Complete
                    && plan.Checkpoints.Any(c => c.Status != CheckpointStatus.Complete))
                    findings.Add(new(section, DoctorLevel.Warn,
                        $"plan '{planId}' says complete but has non-complete checkpoints",
                        "finish or re-open the plan — the statuses contradict each other"));
            }
            catch (VaultException e)
            {
                findings.Add(new(section, DoctorLevel.Fail, $"plan '{planId}' does not parse: {e.Message}",
                    "fix the file — nothing can run against it as-is"));
            }
        }

        if (plans.Count > 0 && findings.All(f => f.Section != section || f.Level is not DoctorLevel.Fail))
            findings.Add(new(section, DoctorLevel.Pass, $"{plans.Count} plan(s) parse clean"));
    }

    private void CheckRepoDrift(List<DoctorFinding> findings, List<Plan> plans)
    {
        const string section = "repo & drift";

        findings.Add(workspace.IsPathIgnored(".orca/") || workspace.IsPathIgnored(".orca")
            ? new(section, DoctorLevel.Pass, ".orca/ is git-ignored (checked via git check-ignore)")
            : new(section, DoctorLevel.Warn, ".orca/ is not git-ignored",
                "the first plan-generate/checkpoint-run adds it; commit that change"));

        var repoName = Path.GetFileName(workspace.RepoRoot);
        var reader = new VaultReader(_layout);
        var stateStore = new RunStateStore(workspace.RepoRoot);

        foreach (var plan in plans)
        {
            if (!string.Equals(plan.TargetRepo, repoName, StringComparison.Ordinal))
                findings.Add(new(section, DoctorLevel.Warn,
                    $"plan '{plan.PlanId}' targets repo '{plan.TargetRepo}' but this repo is '{repoName}'",
                    "run doctor/checkpoint-run from the right repo, or fix target_repo"));

            var state = stateStore.Read(plan.PlanId);
            if (state is not null && plan.Checkpoints.All(c => c.Id != state.CheckpointId))
                findings.Add(new(section, DoctorLevel.Warn,
                    $"run state for '{plan.PlanId}' points at checkpoint '{state.CheckpointId}', which the plan no longer lists",
                    "the plan was edited after the run — inspect .orca/state before running"));

            foreach (var checkpoint in plan.Checkpoints)
            {
                var branch = CheckpointBranch.Name(plan.PlanId, checkpoint.Id);
                var hasRecords = reader.ReadVerificationRecords(plan.PlanId, checkpoint.Id).Count > 0
                    || reader.ReadReconciliationRecords(plan.PlanId, checkpoint.Id).Count > 0;

                if (hasRecords && !workspace.BranchExists(branch) && checkpoint.Status != CheckpointStatus.Complete)
                    findings.Add(new(section, DoctorLevel.Warn,
                        $"'{plan.PlanId}/{checkpoint.Id}' has vault records but no local branch '{branch}'",
                        "these attempts ran on another machine — its repo may hold unpushed commits (BF-09)"));

                if (checkpoint.Status == CheckpointStatus.Complete
                    && workspace.BranchExists(branch)
                    && workspace.CommitSubject(branch) is { } subject
                    && subject.StartsWith($"orca: {plan.PlanId}/{checkpoint.Id} attempt ", StringComparison.Ordinal)
                    && !workspace.IsAncestor(branch, plan.BaseBranch))
                    findings.Add(new(section, DoctorLevel.Warn,
                        $"'{plan.PlanId}/{checkpoint.Id}' is approved on '{branch}' but not merged into '{plan.BaseBranch}'",
                        $"git switch {plan.BaseBranch} && git merge {branch}"));

                if (state?.CheckpointId == checkpoint.Id)
                {
                    var vaultNext = reader.NextAttemptNumber(plan.PlanId, checkpoint.Id);
                    if (vaultNext > state.Attempt + 1)
                        findings.Add(new(section, DoctorLevel.Fail,
                            $"'{plan.PlanId}/{checkpoint.Id}': run state says attempt {state.Attempt}, but vault records already show attempt {vaultNext - 1}",
                            "checkpoint-run will refuse this divergence (BF-01) — reconcile or remove the stale .orca/state"));
                }
            }
        }

        if (findings.All(f => f.Section != section || f.Level is DoctorLevel.Pass or DoctorLevel.Info))
            findings.Add(new(section, DoctorLevel.Pass, "no repo↔vault drift detected"));
    }

    private static readonly Regex DuplicateSuffix = new(@" \(\d+\)\.md$", RegexOptions.Compiled);

    private static bool ResolvesOnPath(string binary)
    {
        if (Path.IsPathRooted(binary))
            return File.Exists(binary);

        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : [""];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0)
                continue;
            foreach (var ext in extensions)
            {
                if (File.Exists(Path.Combine(dir, binary + ext)))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Default probe: run the binary with a 10-second guard, null when it isn't
    /// runnable — doctor's own checks must never hang or crash.</summary>
    private static string? RunProbe(string fileName, string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo);
            if (process is null)
                return null;
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd() : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
