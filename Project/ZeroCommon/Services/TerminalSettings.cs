using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Common.Services;

/// <summary>
/// Which terminal backend a new terminal tab is created with. The choice is
/// per-app (read at tab-creation time), not per-tab, so flipping it affects
/// terminals opened afterwards. Default stays <see cref="EasyConPty"/> — the
/// battle-tested HwndHost + Windows Terminal control — so the modern
/// <see cref="WebViewXterm"/> path is strictly opt-in during the spike.
/// </summary>
public enum TerminalBackend
{
    /// EasyWindowsTerminalControl (HwndHost) → Microsoft.Terminal.Control.dll + conpty.dll.
    EasyConPty,

    /// xterm.js rendered in WebView2, fed by a managed ConPTY host. No HwndHost
    /// airspace — WPF overlays render above the terminal.
    WebViewXterm,
}

/// <summary>Persisted terminal preferences (side-car JSON, mirrors VoiceSettingsStore).</summary>
public sealed class TerminalSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TerminalBackend Backend { get; set; } = TerminalBackend.EasyConPty;
}

/// <summary>
/// JSON persistence for <see cref="TerminalSettings"/> under
/// <c>%LOCALAPPDATA%\AgentZeroLite\terminal-settings.json</c>. Same shape as
/// <c>VoiceSettingsStore</c> / <c>LlmSettingsStore</c>.
/// </summary>
public static class TerminalSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "terminal-settings.json");

    public static TerminalSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new TerminalSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<TerminalSettings>(json) ?? new TerminalSettings();
        }
        catch
        {
            return new TerminalSettings();
        }
    }

    public static void Save(TerminalSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
    }
}
