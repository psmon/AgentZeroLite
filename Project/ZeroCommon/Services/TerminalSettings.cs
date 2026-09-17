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

/// <summary>
/// How the terminal looks. The EasyConPty backend took its colours from a
/// <c>Microsoft.Terminal.Wpf.TerminalTheme</c> built in C#; the WebViewXterm one had
/// them hard-coded in <c>term.js</c> with no ANSI palette at all. Putting the whole
/// thing here means the appearance is owned in one place, is persisted, and no longer
/// requires editing JavaScript to change a colour.
///
/// <para>Colours are <c>#rrggbb</c> strings because that is what xterm.js takes and
/// what a person can read; the ConPTY path converts as it always did.</para>
/// </summary>
public sealed class TerminalTheme
{
    public string Background { get; set; } = "#1e1e1e";
    public string Foreground { get; set; } = "#d4d4d4";
    public string Cursor { get; set; } = "#d4d4d4";
    public string SelectionBackground { get; set; } = "#264f78";

    // The 16 ANSI slots. xterm.js renders every coloured CLI through these, so
    // leaving them unset (the old behaviour) meant the renderer's own defaults
    // rather than anything matching the app.
    public string Black { get; set; } = "#000000";
    public string Red { get; set; } = "#cd3131";
    public string Green { get; set; } = "#0dbc79";
    public string Yellow { get; set; } = "#e5e510";
    public string Blue { get; set; } = "#2472c8";
    public string Magenta { get; set; } = "#bc3fbc";
    public string Cyan { get; set; } = "#11a8cd";
    public string White { get; set; } = "#e5e5e5";
    public string BrightBlack { get; set; } = "#666666";
    public string BrightRed { get; set; } = "#f14c4c";
    public string BrightGreen { get; set; } = "#23d18b";
    public string BrightYellow { get; set; } = "#f5f543";
    public string BrightBlue { get; set; } = "#3b8eea";
    public string BrightMagenta { get; set; } = "#d670d6";
    public string BrightCyan { get; set; } = "#29b8db";
    public string BrightWhite { get; set; } = "#e5e5e5";
}

/// <summary>Persisted terminal preferences (side-car JSON, mirrors VoiceSettingsStore).</summary>
public sealed class TerminalSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TerminalBackend Backend { get; set; } = TerminalBackend.EasyConPty;

    /// <summary>
    /// A CSS font stack, applied by the WebViewXterm backend. Being a web renderer is
    /// the point: the font is a string, fallbacks are free, and changing it needs no
    /// font installed on the machine - JetBrains Mono ships with the app
    /// (<c>Wasm/xterm/vendor/fonts/</c>, OFL 1.1) and the rest of the stack catches
    /// anything a user substitutes.
    /// </summary>
    public string FontFamily { get; set; } = "JetBrains Mono, Cascadia Mono, Consolas, monospace";

    /// <summary>Points. Clamped to a sane range when applied, so a bad edit to the
    /// JSON cannot produce an unreadable or unrenderable terminal.</summary>
    public int FontSize { get; set; } = 14;

    /// <summary>Line height as a multiple of the font size. 1.0 is xterm.js's default
    /// and what a terminal normally wants; a little more helps long sessions.</summary>
    public double LineHeight { get; set; } = 1.0;

    public TerminalTheme Theme { get; set; } = new();

    public const int MinFontSize = 8;
    public const int MaxFontSize = 32;

    /// <summary>The font size actually used, whatever the file says.</summary>
    public int EffectiveFontSize => Math.Clamp(FontSize, MinFontSize, MaxFontSize);

    /// <summary>The line height actually used.</summary>
    public double EffectiveLineHeight => Math.Clamp(LineHeight, 0.8, 2.0);

    /// <summary>Never empty: an empty stack would leave xterm.js measuring a font that
    /// does not exist and mis-sizing every cell.</summary>
    public string EffectiveFontFamily =>
        string.IsNullOrWhiteSpace(FontFamily) ? "monospace" : FontFamily.Trim();
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
