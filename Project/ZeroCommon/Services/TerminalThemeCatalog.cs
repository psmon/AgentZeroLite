namespace Agent.Common.Services;

/// <summary>
/// The terminal colour schemes offered in Settings.
///
/// <para>A palette is 20 colours and nobody wants to type 20 colours, so the UI
/// picks a name and this resolves it. The schemes are the well-known ones a
/// terminal user already recognises, transcribed from their published values —
/// the point is that they look like the thing they are named after, not that
/// they are novel.</para>
///
/// <para><see cref="CustomName"/> is the escape hatch: it means "use the palette
/// stored in <c>terminal-settings.json</c> verbatim", so hand-editing the file
/// still works and the dropdown does not silently overwrite it.</para>
/// </summary>
public static class TerminalThemeCatalog
{
    /// <summary>What a fresh install gets: the palette AgentZero already used.</summary>
    public const string DefaultName = "Dark+";

    /// <summary>Selecting this keeps whatever palette is in the settings file.</summary>
    public const string CustomName = "Custom";

    private static readonly Dictionary<string, Func<TerminalTheme>> Builders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [DefaultName] = () => new TerminalTheme(),   // the type's own defaults
            ["One Dark"] = () => new TerminalTheme
            {
                Background = "#282c34", Foreground = "#abb2bf",
                Cursor = "#528bff", SelectionBackground = "#3e4451",
                Black = "#282c34", Red = "#e06c75", Green = "#98c379", Yellow = "#e5c07b",
                Blue = "#61afef", Magenta = "#c678dd", Cyan = "#56b6c2", White = "#abb2bf",
                BrightBlack = "#5c6370", BrightRed = "#e06c75", BrightGreen = "#98c379",
                BrightYellow = "#e5c07b", BrightBlue = "#61afef", BrightMagenta = "#c678dd",
                BrightCyan = "#56b6c2", BrightWhite = "#ffffff",
            },
            ["Solarized Dark"] = () => new TerminalTheme
            {
                Background = "#002b36", Foreground = "#839496",
                Cursor = "#93a1a1", SelectionBackground = "#073642",
                Black = "#073642", Red = "#dc322f", Green = "#859900", Yellow = "#b58900",
                Blue = "#268bd2", Magenta = "#d33682", Cyan = "#2aa198", White = "#eee8d5",
                BrightBlack = "#002b36", BrightRed = "#cb4b16", BrightGreen = "#586e75",
                BrightYellow = "#657b83", BrightBlue = "#839496", BrightMagenta = "#6c71c4",
                BrightCyan = "#93a1a1", BrightWhite = "#fdf6e3",
            },
            ["Gruvbox Dark"] = () => new TerminalTheme
            {
                Background = "#282828", Foreground = "#ebdbb2",
                Cursor = "#ebdbb2", SelectionBackground = "#504945",
                Black = "#282828", Red = "#cc241d", Green = "#98971a", Yellow = "#d79921",
                Blue = "#458588", Magenta = "#b16286", Cyan = "#689d6a", White = "#a89984",
                BrightBlack = "#928374", BrightRed = "#fb4934", BrightGreen = "#b8bb26",
                BrightYellow = "#fabd2f", BrightBlue = "#83a598", BrightMagenta = "#d3869b",
                BrightCyan = "#8ec07c", BrightWhite = "#ebdbb2",
            },
            ["Nord"] = () => new TerminalTheme
            {
                Background = "#2e3440", Foreground = "#d8dee9",
                Cursor = "#d8dee9", SelectionBackground = "#434c5e",
                Black = "#3b4252", Red = "#bf616a", Green = "#a3be8c", Yellow = "#ebcb8b",
                Blue = "#81a1c1", Magenta = "#b48ead", Cyan = "#88c0d0", White = "#e5e9f0",
                BrightBlack = "#4c566a", BrightRed = "#bf616a", BrightGreen = "#a3be8c",
                BrightYellow = "#ebcb8b", BrightBlue = "#81a1c1", BrightMagenta = "#b48ead",
                BrightCyan = "#8fbcbb", BrightWhite = "#eceff4",
            },
            ["Tokyo Night"] = () => new TerminalTheme
            {
                Background = "#1a1b26", Foreground = "#c0caf5",
                Cursor = "#c0caf5", SelectionBackground = "#33467c",
                Black = "#15161e", Red = "#f7768e", Green = "#9ece6a", Yellow = "#e0af68",
                Blue = "#7aa2f7", Magenta = "#bb9af7", Cyan = "#7dcfff", White = "#a9b1d6",
                BrightBlack = "#414868", BrightRed = "#f7768e", BrightGreen = "#9ece6a",
                BrightYellow = "#e0af68", BrightBlue = "#7aa2f7", BrightMagenta = "#bb9af7",
                BrightCyan = "#7dcfff", BrightWhite = "#c0caf5",
            },
        };

    /// <summary>Preset names in display order, with <see cref="CustomName"/> last.</summary>
    public static IReadOnlyList<string> Names { get; } =
        Builders.Keys.Append(CustomName).ToList();

    public static bool IsPreset(string? name) =>
        name is not null && Builders.ContainsKey(name);

    /// <summary>
    /// The palette for a name, or null for <see cref="CustomName"/> and anything
    /// unrecognised — the caller then uses whatever the settings file holds, which
    /// is what makes a hand-edited palette survive.
    /// </summary>
    public static TerminalTheme? Get(string? name) =>
        name is not null && Builders.TryGetValue(name, out var build) ? build() : null;
}
