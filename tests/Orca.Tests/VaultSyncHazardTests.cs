using Orca.Core.Domain;
using Orca.Core.Vault;

namespace Orca.Tests;

/// <summary>BF-09: the two-machine hazards — sync-conflict artifacts must not brick a
/// plan's record listings, and silently skipped plan directories get named.</summary>
public sealed class VaultSyncHazardTests : IDisposable
{
    private readonly string _vault = Directory.CreateTempSubdirectory("orca-sync-vault-").FullName;
    private readonly VaultLayout _layout;

    public VaultSyncHazardTests() => _layout = new VaultLayout(_vault, "Plans");

    public void Dispose() => Directory.Delete(_vault, recursive: true);

    [Fact]
    public void Record_listings_skip_sync_conflict_artifacts_instead_of_bricking_the_plan()
    {
        Directory.CreateDirectory(Path.Combine(_vault, "Plans", "recipe-tagging"));
        new VaultWriter(_layout).WriteVerificationRecord(new VerificationRecord(
            1, "recipe-tagging", 1, "01-tag-entity", 1,
            new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero),
            new GateResults(GateOutcome.Pass, GateOutcome.Pass),
            [new SemanticCriterionResult("works", SemanticVerdict.Pass, null)],
            VerificationVerdict.Pass, "Body.\n"));

        // The artifact matches the old glob and its parse used to throw until a human
        // hand-deleted it — Prepare, verification, and status were all bricked.
        File.WriteAllText(
            Path.Combine(_layout.VerificationDirectory("recipe-tagging"),
                "01-tag-entity.attempt-1.sync-conflict-20260716-ABCDEF.md"),
            "conflicted copy");

        var records = new VaultReader(_layout).ReadVerificationRecords("recipe-tagging", "01-tag-entity");

        Assert.Single(records);
        Assert.Equal(1, records[0].Attempt);
    }

    [Fact]
    public void Skipped_plan_directories_are_named_with_actionable_reasons()
    {
        // A case-mismatched spine: works on Windows, invisible on Linux.
        Directory.CreateDirectory(Path.Combine(_vault, "Plans", "foo"));
        File.WriteAllText(Path.Combine(_vault, "Plans", "foo", "FOO.md"), "x");
        // An uppercase directory name: never a valid plan id.
        Directory.CreateDirectory(Path.Combine(_vault, "Plans", "Bar"));
        // A healthy plan directory produces no warning.
        Directory.CreateDirectory(Path.Combine(_vault, "Plans", "ok-plan"));
        File.WriteAllText(Path.Combine(_vault, "Plans", "ok-plan", "ok-plan.md"), "x");

        var warnings = PlanLocator.ListSkippedPlanDirectories(_layout);

        Assert.Contains(warnings, w => w.Contains("found 'FOO.md', expected 'foo.md'"));
        Assert.Contains(warnings, w => w.Contains("'Bar'") && w.Contains("lowercase"));
        Assert.DoesNotContain(warnings, w => w.Contains("ok-plan"));
    }
}
