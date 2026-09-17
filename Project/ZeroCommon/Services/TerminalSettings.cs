using System.Text.Json;

namespace Agent.Common.Services;

/// <summary>
/// How the terminal looks. The colours were once hard-coded in <c>term.js</c> —
/// a background and a foreground, with no ANSI palette at all, so every coloured
/// CLI rendered through xterm.js's own defaults. Owning the whole palette here
/// means appearance is in one place, is persisted, and changing a colour is not a
/// JavaScript edit.
///
/// <para>Colours are <c>#rrggbb</c> strings because that is what xterm.js takes and
/// what a person can read.</para>
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

    /// <summary>
    /// Which palette from <see cref="TerminalThemeCatalog"/>. The named presets are
    /// resolved on read, so switching themes in Settings does not rewrite - or lose -
    /// a palette someone hand-edited into <see cref="Theme"/>; selecting
    /// <c>Custom</c> brings that one back.
    /// </summary>
    public string ThemeName { get; set; } = TerminalThemeCatalog.DefaultName;

    /// <summary>The stored palette. Used verbatim when <see cref="ThemeName"/> is
    /// <c>Custom</c> or names a preset this build does not know.</summary>
    /// <summary>
    /// Force the cursor to blink regardless of what the program in the terminal
    /// asks for. Off, so DECSCUSR wins: a TUI that draws its own cursor and requests
    /// a steady one gets a steady one. Forcing this on made the cursor blink under
    /// applications that had explicitly asked it not to.
    /// </summary>
    public bool CursorBlink { get; set; } = false;

    /// <summary>
    /// Which xterm.js renderer to use. DOM is the library default and costs nothing
    /// extra; WebGL draws through a per-terminal texture atlas, which is faster for
    /// heavy output but holds GPU and host memory for every open terminal. It is
    /// off by default because it is a cost, not a fix — the cursor wandering under
    /// Ink TUIs was synchronized output, not renderer throughput.
    /// </summary>
    public bool UseWebGlRenderer { get; set; } = false;

    public TerminalTheme Theme { get; set; } = new();

    /// <summary>The palette actually applied.</summary>
    public TerminalTheme EffectiveTheme => TerminalThemeCatalog.Get(ThemeName) ?? Theme;

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
