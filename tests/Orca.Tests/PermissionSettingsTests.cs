using System.Text.Json;
using Orca.Core.Execution;

namespace Orca.Tests;

public class PermissionSettingsTests
{
    [Fact]
    public void Payload_matches_the_probed_permissions_deny_shape()
    {
        // The shape empirically verified on 2.1.201 (2026-07-06): permissions.deny rules
        // passed via --settings survive --safe-mode and win over --allowedTools.
        var json = PermissionSettings.ToJson(["Bash(git push*)", "Bash(rm *)"]);

        using var doc = JsonDocument.Parse(json);
        var deny = doc.RootElement.GetProperty("permissions").GetProperty("deny");
        Assert.Equal(2, deny.GetArrayLength());
        Assert.Equal("Bash(git push*)", deny[0].GetString());
        Assert.Equal("Bash(rm *)", deny[1].GetString());
        Assert.Single(doc.RootElement.EnumerateObject());
    }

    [Fact]
    public void Patterns_pass_verbatim()
    {
        // Same sharp edge as the allowlist: `Bash(rm *)` and `Bash(rm*)` deny different
        // command sets; never normalize.
        var json = PermissionSettings.ToJson(["Bash(rm *)"]);
        Assert.Contains("Bash(rm *)", json);
        Assert.DoesNotContain("Bash(rm*)", json);
    }

    [Fact]
    public void Refuses_an_empty_deny_list() =>
        Assert.Throws<ExecutorException>(() => PermissionSettings.ToJson([]));

    [Fact]
    public void Refuses_a_blank_pattern() =>
        Assert.Throws<ExecutorException>(() => PermissionSettings.ToJson(["Bash(rm *)", ""]));
}
