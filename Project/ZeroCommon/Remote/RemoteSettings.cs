namespace Agent.Common.Remote;

/// <summary>
/// Persisted options for the Remote (web terminal control) feature. Kept small and
/// WPF-free so <c>remote-settings.json</c> sits alongside the other JSON stores under
/// <c>%LOCALAPPDATA%\AgentZeroLite\</c>. Mirrors <see cref="Agent.Common.Vision.VisionSettings"/>.
/// </summary>
public sealed class RemoteSettings
{
    /// <summary>Whether the remote server should be running. Off by default — the feature
    /// exposes terminal control over the network, so it must be an explicit opt-in.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>TCP port the HttpListener binds. Default 8787.</summary>
    public int Port { get; set; } = 8787;

    /// <summary>Bind address. "0.0.0.0" = reachable on the LAN (default, per product
    /// decision), "127.0.0.1" = loopback only.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Maximum number of concurrent remote connections. Default 3.</summary>
    public int MaxConnections { get; set; } = 3;

    /// <summary>
    /// SHA-256 hashes (lowercase hex) of the bearer tokens issued to paired browsers.
    /// Tokens are stored one-way — the raw token is shown to the browser exactly once
    /// and never persisted, so a leaked settings file cannot be replayed as a credential
    /// (strictly stronger than reversibly encrypting the token at rest). An entry is
    /// added on successful PIN pairing and removed on revoke.
    /// </summary>
    public List<string> PairedTokenHashes { get; set; } = new();

    /// <summary>Optional "group:tab" default target a fresh connection auto-attaches to.
    /// Empty = attach to the currently active terminal.</summary>
    public string DefaultTarget { get; set; } = "";
}
