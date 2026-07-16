using Orca.Core.Planning;

namespace Orca.Tests;

/// <summary>SUG-01: the Scout's assembled prompt — role text + request block, with the
/// audit path given ABSOLUTE (the Scout's cwd is the target repo, not the vault).</summary>
public sealed class ScoutPromptTests
{
    private static readonly PlanningRequest Request = new()
    {
        PlanId = "recipe-tagging",
        Goal = "Add tag CRUD to the API.",
        Title = null,
        TargetRepoName = "RecipeApp",
        BaseBranch = "main",
    };

    private const string AuditPath = "/vault/Plans/recipe-tagging/recipe-tagging.audit.md";

    [Fact]
    public void Initial_prompt_carries_the_role_text_request_and_absolute_path()
    {
        var prompt = ScoutPrompt.AssembleInitial(Request, AuditPath, "2026-07-17T09:00:00Z");

        Assert.Contains("# Orca Scout", prompt);
        Assert.Contains("plan_id: recipe-tagging", prompt);
        Assert.Contains("ABSOLUTE path", prompt);
        Assert.Contains(AuditPath, prompt);
        Assert.Contains("2026-07-17T09:00:00Z", prompt);
        Assert.Contains("Add tag CRUD to the API.", prompt);
    }

    [Fact]
    public void Role_text_contracts_the_five_sections_and_the_untouched_repo()
    {
        foreach (var section in Orca.Core.Vault.AuditNoteParser.RequiredSections)
            Assert.Contains(section, ScoutPrompt.RoleText);
        Assert.Contains("byte-for-byte untouched", ScoutPrompt.RoleText);
        Assert.Contains("type: audit", ScoutPrompt.RoleText);
    }

    [Fact]
    public void Retry_prompt_carries_the_exact_error()
    {
        var prompt = ScoutPrompt.AssembleRetry(
            Request, AuditPath, "2026-07-17T09:00:00Z", "audit body is missing the contracted section '## Risks'");

        Assert.Contains("Retry — your previous output failed validation", prompt);
        Assert.Contains("missing the contracted section", prompt);
    }

    [Fact]
    public void Role_prompt_hash_is_stable_and_recorded()
    {
        Assert.Equal(64, ScoutPrompt.RoleSha256.Length);
    }
}
