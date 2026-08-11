using Agent.Common.Agents;

namespace ZeroCommon.Tests;

/// <summary>Headless tests for integration authority precedence (herdr-adoption H4).</summary>
[Trait("Category", "AgentIntegration")]
public sealed class AgentIntegrationCatalogTests
{
    [Fact]
    public void Lookup_SessionIdentity_And_LifecycleAuthority()
    {
        Assert.Equal(IntegrationAuthority.SessionIdentity, AgentIntegrationCatalog.Lookup("Claude 1")!.Authority);
        Assert.Equal(IntegrationAuthority.LifecycleAuthority, AgentIntegrationCatalog.Lookup("opencode")!.Authority);
    }

    [Fact]
    public void Lookup_Unknown_IsNull()
    {
        Assert.Null(AgentIntegrationCatalog.Lookup("RTX-NOTE"));
    }

    [Fact]
    public void SessionIdentity_AlwaysUsesScreenDetection()
    {
        // Claude: screen (H1) drives state whether or not a hook is installed.
        Assert.True(AgentIntegrationCatalog.UseScreenDetection("claude", hookInstalled: false));
        Assert.True(AgentIntegrationCatalog.UseScreenDetection("claude", hookInstalled: true));
    }

    [Fact]
    public void LifecycleAuthority_HookSuppressesScreen()
    {
        // opencode: hook authors state → screen suppressed only when the hook is installed.
        Assert.True(AgentIntegrationCatalog.UseScreenDetection("opencode", hookInstalled: false));
        Assert.False(AgentIntegrationCatalog.UseScreenDetection("opencode", hookInstalled: true));
    }

    [Fact]
    public void UnknownAgent_UsesScreenDetection()
    {
        Assert.True(AgentIntegrationCatalog.UseScreenDetection("some-random-cli", hookInstalled: true));
    }
}
