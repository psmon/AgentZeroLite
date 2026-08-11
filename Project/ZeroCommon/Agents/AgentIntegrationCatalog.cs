using System;
using System.Collections.Generic;
using System.Linq;

namespace Agent.Common.Agents;

/// <summary>
/// Which source is authoritative for an agent's lifecycle state (herdr-adoption H4).
///   LifecycleAuthority — the CLI's hooks author state; screen detection is
///                        suppressed while a hook is installed (hook wins).
///   SessionIdentity    — hooks report only a resumable session id; the state
///                        always comes from screen detection.
/// </summary>
public enum IntegrationAuthority { LifecycleAuthority, SessionIdentity }

/// <summary>One agent CLI's integration profile.</summary>
public sealed record AgentIntegration(string Agent, IntegrationAuthority Authority, string HookConfigHint);

/// <summary>
/// Catalog of agent-CLI integrations + the authority-precedence rule (H4). Encodes
/// herdr's "one source of truth per pane" model as data: for lifecycle-authority
/// CLIs an installed hook overrides screen detection; for session-identity CLIs the
/// screen manifest (H1) always drives state and the hook only supplies a resume id.
/// Pure &amp; testable; the actual per-CLI hook-file writing is a WPF-side installer.
/// </summary>
public static class AgentIntegrationCatalog
{
    private static readonly AgentIntegration[] Table =
    {
        // Session-identity: state from screen (H1), hook reports resume id only.
        new("claude",  IntegrationAuthority.SessionIdentity,    "~/.claude*/settings.json (hooks)"),
        new("codex",   IntegrationAuthority.SessionIdentity,    "~/.codex/hooks.json"),
        new("cursor",  IntegrationAuthority.SessionIdentity,    "~/.cursor/hooks.json"),
        new("copilot", IntegrationAuthority.SessionIdentity,    "~/.copilot/config.json"),
        new("devin",   IntegrationAuthority.SessionIdentity,    "devin hooks"),
        new("droid",   IntegrationAuthority.SessionIdentity,    "droid hooks"),
        new("grok",    IntegrationAuthority.SessionIdentity,    "grok hooks"),
        // Lifecycle-authority: hooks author state, screen detection suppressed.
        new("opencode", IntegrationAuthority.LifecycleAuthority, "opencode plugin"),
        new("kilo",     IntegrationAuthority.LifecycleAuthority, "kilo plugin"),
        new("kimi",     IntegrationAuthority.LifecycleAuthority, "kimi hooks"),
        new("pi",       IntegrationAuthority.LifecycleAuthority, "pi hooks"),
        new("omp",      IntegrationAuthority.LifecycleAuthority, "omp hooks"),
        new("mastra",   IntegrationAuthority.LifecycleAuthority, "mastra hooks"),
    };

    /// <summary>Finds the integration profile for an agent/tab name (substring), or null.</summary>
    public static AgentIntegration? Lookup(string? agentName)
    {
        var n = (agentName ?? "").ToLowerInvariant();
        return Table.FirstOrDefault(e => n.Contains(e.Agent));
    }

    /// <summary>
    /// Authority precedence (H4): should the screen manifest (H1) drive this
    /// agent's state? True unless the agent is a lifecycle-authority CLI whose
    /// hook is installed (then the hook is the single source of truth).
    /// </summary>
    public static bool UseScreenDetection(string? agentName, bool hookInstalled)
    {
        var integ = Lookup(agentName);
        if (integ is null) return true; // unknown → generic screen detection
        if (integ.Authority == IntegrationAuthority.LifecycleAuthority && hookInstalled)
            return false;               // hook authors state → suppress screen
        return true;
    }

    /// <summary>All catalogued integrations.</summary>
    public static IReadOnlyList<AgentIntegration> All => Table;
}
