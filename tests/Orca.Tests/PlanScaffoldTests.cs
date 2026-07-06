using Orca.Core.Domain;
using Orca.Core.Vault;

namespace Orca.Tests;

public sealed class PlanScaffoldTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 18, 0, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "orca-tests-" + Guid.NewGuid().ToString("N"));

    public PlanScaffoldTests() => Directory.CreateDirectory(_root);

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

    private VaultLayout Layout() => new(_root, "Plans");

    [Fact]
    public void Skeleton_round_trips_through_the_real_parser()
    {
        var path = PlanScaffold.CreatePlan(Layout(), "recipe-tagging", "Add tagging", "RecipeApp", "main", Now);

        var plan = new VaultReader(Layout()).ReadPlan("recipe-tagging");
        Assert.Equal(Layout().PlanFile("recipe-tagging"), path);
        Assert.Equal(PlanStatus.Draft, plan.Status);
        Assert.Equal("Add tagging", plan.Title);
        Assert.Equal("RecipeApp", plan.TargetRepo);
        Assert.Equal("main", plan.BaseBranch);
        Assert.Equal(Now, plan.Created);
        Assert.Single(plan.Checkpoints);
    }

    [Fact]
    public void Titles_needing_yaml_escaping_survive_the_round_trip()
    {
        PlanScaffold.CreatePlan(Layout(), "tricky", "Fix: the \"big\" bug \\ regression", "Repo", "main", Now);

        var plan = new VaultReader(Layout()).ReadPlan("tricky");
        Assert.Equal("Fix: the \"big\" bug \\ regression", plan.Title);
    }

    [Fact]
    public void Refuses_to_overwrite_an_existing_plan()
    {
        PlanScaffold.CreatePlan(Layout(), "p", "Title", "Repo", "main", Now);

        var e = Assert.Throws<VaultException>(() =>
            PlanScaffold.CreatePlan(Layout(), "p", "Other", "Repo", "main", Now));
        Assert.Contains("refusing to overwrite", e.Message);
    }

    [Fact]
    public void Rejects_an_invalid_plan_id() =>
        Assert.ThrowsAny<ArgumentException>(() =>
            PlanScaffold.CreatePlan(Layout(), "Bad Id", "T", "Repo", "main", Now));
}

public sealed class PlanLocatorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "orca-tests-" + Guid.NewGuid().ToString("N"));

    public PlanLocatorTests() => Directory.CreateDirectory(_root);

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

    private VaultLayout Layout() => new(_root, "Plans");

    private void AddPlanFolder(string planId)
    {
        var dir = Path.Combine(_root, "Plans", planId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, planId + ".md"), "stub — locator never parses");
    }

    [Fact]
    public void Explicit_id_wins_without_touching_the_disk()
    {
        Assert.Equal("anything", PlanLocator.ResolvePlanId(Layout(), "anything"));
    }

    [Fact]
    public void A_single_plan_is_found_automatically()
    {
        AddPlanFolder("only-plan");
        Directory.CreateDirectory(Path.Combine(_root, "Plans", "not-a-plan-no-file"));

        Assert.Equal("only-plan", PlanLocator.ResolvePlanId(Layout(), null));
    }

    [Fact]
    public void Zero_plans_fail_loudly()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Plans"));

        var e = Assert.Throws<VaultException>(() => PlanLocator.ResolvePlanId(Layout(), null));
        Assert.Contains("plan-init", e.Message);
    }

    [Fact]
    public void Multiple_plans_demand_an_explicit_choice()
    {
        AddPlanFolder("plan-a");
        AddPlanFolder("plan-b");

        var e = Assert.Throws<VaultException>(() => PlanLocator.ResolvePlanId(Layout(), null));
        Assert.Contains("plan-a", e.Message);
        Assert.Contains("plan-b", e.Message);
        Assert.Contains("--plan", e.Message);
    }
}
