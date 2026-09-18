namespace Agent.Common.Wearable;

/// <summary>
/// Which brain answers the watch. The wearable host is a second process, so the
/// choice is about <i>where the tokens are spent</i>, not about which UI is open.
/// </summary>
public static class WearableBrainNames
{
    /// <summary>
    /// AgentZero's own agent loop (<c>Agent.Common.Llm.Tools.IAgentLoop</c>) against the
    /// External provider configured in Settings → LLM. One HTTP endpoint, no model in
    /// this process — the cheap default.
    /// </summary>
    public const string AgentExternal = "AgentExternal";

    /// <summary>
    /// The same agent loop against an on-device GGUF from AgentZero's model catalog.
    /// The host loads its <b>own</b> LLamaSharp context: running this while the GUI has
    /// a model loaded means two copies in VRAM, so it is opt-in.
    /// </summary>
    public const string AgentLocal = "AgentLocal";

    /// <summary>
    /// Shell out to an agent CLI (claude / netclaw / echo) per
    /// <see cref="WearableSettings.CliProvider"/>. The Chat app's original path — kept
    /// because a CLI agent is a whole agent, tools included.
    /// </summary>
    public const string Cli = "Cli";

    public static readonly IReadOnlyList<string> All = new[] { AgentExternal, AgentLocal, Cli };
}

/// <summary>
/// One folder the watch's file tools may reach (M0032). <see cref="Alias"/> is the name the
/// model uses (<c>docs/notes.txt</c>); <see cref="Path"/> is where that really is on disk.
/// </summary>
public sealed class AllowedRoot
{
    public string Alias { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>
    /// Whether <c>write_file</c> / <c>edit_file</c> may touch this folder. Off by default: a
    /// device across the room reading a folder and one rewriting it are different grants.
    /// </summary>
    public bool Writable { get; set; }

    /// <summary>Lower-case letters, digits, <c>-</c> and <c>_</c>, at most 32 characters; anything else is dropped.</summary>
    public static string NormalizeAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return "";
        var chars = alias.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : (c is '-' or '_' ? c : '-'))
            .ToArray();
        var s = new string(chars).Trim('-', '_');
        return s.Length > 32 ? s[..32] : s;
    }
}

/// <summary>
/// Persisted options for the Wearable host (M0031) — the second process that owns the
/// watch's single BLE link and serves its three apps (AskBot over Akka remoting, Chat
/// over the line protocol, Claude HUD over :8765).
///
/// Everything model-shaped is deliberately <b>absent</b> here: the voice, the STT model
/// and the LLM endpoint come from AgentZero's existing stores
/// (<c>VoiceSettingsStore</c> / <c>LlmSettingsStore</c>), so the watch speaks with the
/// same voice and thinks with the same model the app is already configured for. This
/// file only carries what is specific to the wearable link itself.
///
/// Mirrors <see cref="Agent.Common.Remote.RemoteSettings"/>: WPF-free POCO,
/// <c>wearable-settings.json</c> under <c>%LOCALAPPDATA%\AgentZeroLite\</c>.
/// </summary>
public sealed class WearableSettings
{
    /// <summary>
    /// Whether the host should be running. Off by default — it holds a radio and answers
    /// a device in the room, so it is an explicit opt-in like Remote.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// BLE advertised name of the watch. The firmware advertises only while nothing is
    /// connected, which is why the host retries rather than scanning once.
    /// </summary>
    public string DeviceName { get; set; } = "claude-hud";

    /// <summary>
    /// Skip the radio entirely. The Akka remoting port still listens, so a PC peer (or
    /// <c>cpp/askbot_cli</c>) can exercise the whole conversation path with no board.
    /// </summary>
    public bool DisableBle { get; set; } = false;

    /// <summary>Akka classic-remoting port the BLE tunnel relays into. Default 2552.</summary>
    public int RemotingPort { get; set; } = 2552;

    /// <summary>
    /// Address remoting binds. Loopback by default: the tunnel connects from inside this
    /// process, so nothing needs to reach it from the network unless a PC peer is testing.
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>Akka system name. The device's C++ client dials <c>akka.tcp://{this}@host:port</c>.</summary>
    public string SystemName { get; set; } = "AskBot";

    /// <summary>
    /// Serve the Claude HUD endpoint (<c>POST /status</c>, <c>POST /event</c>). Keeps the
    /// byte-for-byte contract the watch's <c>hud_ble</c> firmware already speaks, so
    /// hooks installed in <c>~/.claude</c> need no change.
    /// </summary>
    public bool HudEnabled { get; set; } = true;

    /// <summary>Port for that endpoint. 8765 is what the installed hooks post to.</summary>
    public int HudPort { get; set; } = 8765;

    /// <summary>One of <see cref="WearableBrainNames"/>.</summary>
    public string Brain { get; set; } = WearableBrainNames.AgentExternal;

    /// <summary>
    /// Provider key when <see cref="Brain"/> is <see cref="WearableBrainNames.Cli"/>.
    /// "echo" is the offline loopback that needs no LLM at all.
    /// </summary>
    public string CliProvider { get; set; } = "echo";

    /// <summary>
    /// Catalog id of the GGUF to load when <see cref="Brain"/> is
    /// <see cref="WearableBrainNames.AgentLocal"/>. Empty → the host reports why it
    /// cannot start rather than downloading anything.
    /// </summary>
    public string LocalModelId { get; set; } = "";

    /// <summary>
    /// Legacy single folder for the watch's file tools (pre-M0032). Kept only so an older
    /// <c>wearable-settings.json</c> still loads: <see cref="Normalize"/> migrates a non-empty
    /// value into <see cref="AllowedRoots"/> (alias <c>workspace</c>, writable) and clears it.
    /// New code reads <see cref="AllowedRoots"/> only.
    /// </summary>
    public string WorkspaceRoot { get; set; } = "";

    /// <summary>
    /// The folders the watch's file tools may reach (M0032). Empty = no file tools at all
    /// (default-deny): a device across the room should not reach the whole disk because
    /// nobody chose a root. The model addresses files as <c>alias/relative/path</c>; the
    /// real path never leaves this process.
    /// </summary>
    public List<AllowedRoot> AllowedRoots { get; set; } = new();

    /// <summary>
    /// Whether the watch's agent may search the web and read pages (M0032). When the GUI is
    /// running the pages open in its Browser page; otherwise the host fetches headlessly.
    /// </summary>
    public bool WebToolsEnabled { get; set; } = true;

    /// <summary>Hard cap on the page text one <c>web_read</c> hands the model.</summary>
    public int WebMaxChars { get; set; } = 6000;

    /// <summary>
    /// Explicit path of <c>AgentZeroLite.exe</c> for the web tools' GUI bridge. Empty = resolve
    /// from the host's own location (shipped layout: the parent folder; dev layout: the
    /// AgentZeroWpf build output). <c>off</c> = never use the GUI: browse headlessly even
    /// while it runs (nothing opens on screen).
    /// </summary>
    public string GuiExePath { get; set; } = "";

    /// <summary>
    /// Bring a loaded file into the shape the host expects: migrate the legacy
    /// <see cref="WorkspaceRoot"/>, drop roots without a path, give alias-less roots the
    /// folder's name, and make aliases unique. Idempotent; called by the store on load.
    /// </summary>
    public WearableSettings Normalize()
    {
        AllowedRoots ??= new List<AllowedRoot>();
        if (AllowedRoots.Count == 0 && !string.IsNullOrWhiteSpace(WorkspaceRoot))
            AllowedRoots.Add(new AllowedRoot { Alias = "workspace", Path = WorkspaceRoot.Trim(), Writable = true });
        WorkspaceRoot = "";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<AllowedRoot>();
        foreach (var root in AllowedRoots)
        {
            if (root is null || string.IsNullOrWhiteSpace(root.Path)) continue;
            root.Path = root.Path.Trim();
            var alias = AllowedRoot.NormalizeAlias(root.Alias);
            if (alias.Length == 0)
                alias = AllowedRoot.NormalizeAlias(System.IO.Path.GetFileName(root.Path.TrimEnd('\\', '/')));
            if (alias.Length == 0) alias = "root";
            var unique = alias;
            for (var n = 2; !seen.Add(unique); n++) unique = $"{alias}-{n}";
            root.Alias = unique;
            kept.Add(root);
        }
        AllowedRoots = kept;
        if (WebMaxChars < 500) WebMaxChars = 500;
        return this;
    }

    /// <summary>
    /// Style instructions prepended for the CLI brain. The agent brains carry their own
    /// system prompt, so this is not applied to them — an echoing CLI would otherwise
    /// have the watch read the instructions aloud.
    /// </summary>
    public string ReplyStyle { get; set; } =
        "You are answering on a small round watch-like display. Reply in the user's language. " +
        "Plain text only: no markdown, no bullet lists, no code blocks, and never include URLs, " +
        "links or citations. At most 3 short sentences.";

    /// <summary>Hard cap on a reply before it is cut. The screen holds far less than this.</summary>
    public int MaxReplyChars { get; set; } = 1200;

    /// <summary>
    /// Bytes per reply chunk. One Akka frame holds far more, but the device appends
    /// chunks into a fixed buffer and redraws per chunk.
    /// </summary>
    public int ChunkBytes { get; set; } = 400;

    /// <summary>
    /// Spoken to a device the moment it connects. Empty = say nothing. A push
    /// notification, and the way to exercise screen + speaker without touching the watch.
    /// </summary>
    public string AnnounceOnConnect { get; set; } = "";

    /// <summary>
    /// Ask a connecting device to record for this many milliseconds. 0 = off. Test aid,
    /// and a real capability: the host can start an utterance itself.
    /// </summary>
    public int TalkOnConnectMs { get; set; } = 0;

    /// <summary>
    /// Captures whose peak/RMS are below these (dBFS) never reach Whisper. Left to itself
    /// on a quiet capture whisper.cpp invents text — "[구독 / 좋아요]" and similar training
    /// boilerplate — and the watch would then ask the model about it. Measured on the
    /// reference board: an empty room is rms -47..-61 dBFS, speech is -32..-31.
    /// </summary>
    public double SilencePeakDb { get; set; } = -45;

    /// <inheritdoc cref="SilencePeakDb"/>
    public double SilenceRmsDb { get; set; } = -45;

    /// <summary>Captures longer than this are truncated before transcription.</summary>
    public int MaxCaptureSeconds { get; set; } = 60;
}
