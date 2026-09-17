using System.Text;
using System.Text.Json;

namespace Agent.Common.Services;

/// <summary>
/// One key combination, parsed from and written back as text like
/// <c>Ctrl+Shift+Right</c>.
///
/// <para>The key is kept as a <b>string</b> rather than <c>System.Windows.Input.Key</c>
/// so this whole file stays WPF-free and testable headlessly; the WPF side maps the
/// name once, at match time. The modifiers are a small flags enum of our own for the
/// same reason.</para>
/// </summary>
public readonly record struct ShortcutGesture(ShortcutModifiers Modifiers, string Key)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Key);

    /// <summary>
    /// Parse <c>Ctrl+Alt+Right</c>. Order and case do not matter, and the common
    /// spellings are accepted (Control/Ctrl, Win/Windows/Meta) because this is typed
    /// by hand into a settings file as often as it is picked in the UI.
    /// </summary>
    public static bool TryParse(string? text, out ShortcutGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var mods = ShortcutModifiers.None;
        string? key = null;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;

            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ShortcutModifiers.Control; continue;
                case "shift": mods |= ShortcutModifiers.Shift; continue;
                case "alt": mods |= ShortcutModifiers.Alt; continue;
                case "win" or "windows" or "meta": mods |= ShortcutModifiers.Windows; continue;
            }

            // Two keys in one gesture is a typo, not a chord.
            if (key is not null) return false;
            key = part;
        }

        if (key is null) return false;

        // A bare letter would fire while typing in the terminal, which is where the
        // user spends all their time. At least one modifier, always.
        if (mods == ShortcutModifiers.None) return false;

        gesture = new ShortcutGesture(mods, Normalize(key));
        return true;
    }

    /// <summary>Canonical spelling, so two settings files that mean the same thing look the same.</summary>
    public override string ToString()
    {
        if (IsEmpty) return "";
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(ShortcutModifiers.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(ShortcutModifiers.Windows)) sb.Append("Win+");
        return sb.Append(Key).ToString();
    }

    private static string Normalize(string key) =>
        key.Length == 1 ? key.ToUpperInvariant()
                        : char.ToUpperInvariant(key[0]) + key[1..].ToLowerInvariant();
}

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// Keyboard shortcuts for the window commands in <see cref="WindowCommandIds"/>.
///
/// <para><b>Off by default.</b> The app had no shortcuts before, so every combination
/// is currently free for whatever is running inside a terminal — a CLI, an editor,
/// a TUI. Turning them on takes keys away from those programs, and that is the
/// user's call to make, not a default to inherit on upgrade.</para>
/// </summary>
public sealed class ShortcutSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>Command id → gesture text. Absent or blank means unbound.</summary>
    public Dictionary<string, string> Bindings { get; set; } = new();

    /// <summary>
    /// A starting point offered in Settings, not applied on its own. Alt-based so it
    /// stays clear of Ctrl+Shift, which the bottom-panel tabs and the bot dock already
    /// use, and of the Ctrl combinations terminal programs rely on.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Suggested { get; } = new Dictionary<string, string>
    {
        [WindowCommandIds.SplitRight] = "Ctrl+Alt+Right",
        [WindowCommandIds.SplitDown] = "Ctrl+Alt+Down",
        [WindowCommandIds.Float] = "Ctrl+Alt+F",
        [WindowCommandIds.Dock] = "Ctrl+Alt+D",
        [WindowCommandIds.TerminalAdd] = "Ctrl+Alt+T",
        [WindowCommandIds.PanelToggle] = "Ctrl+Alt+J",
        [WindowCommandIds.PanelMaximize] = "Ctrl+Alt+M",
    };

    /// <summary>
    /// The bindings that are actually usable: known command, parseable gesture, and
    /// the first one wins if two commands claim the same keys — a duplicate is a
    /// mistake, and silently running both would be worse than running one.
    /// </summary>
    public IReadOnlyList<(ShortcutGesture Gesture, string CommandId)> ResolveBindings()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(ShortcutGesture, string)>();

        foreach (var (id, text) in Bindings)
        {
            if (!WindowCommandIds.IsKnown(id)) continue;
            if (!ShortcutGesture.TryParse(text, out var gesture)) continue;
            if (!seen.Add(gesture.ToString())) continue;
            list.Add((gesture, id));
        }
        return list;
    }

    /// <summary>Command ids bound to the same keys as something earlier — surfaced in
    /// Settings so a typo is visible rather than just inert.</summary>
    public IReadOnlyList<string> FindConflicts()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dupes = new List<string>();
        foreach (var (id, text) in Bindings)
        {
            if (!WindowCommandIds.IsKnown(id)) continue;
            if (!ShortcutGesture.TryParse(text, out var gesture)) continue;
            if (!seen.Add(gesture.ToString())) dupes.Add(id);
        }
        return dupes;
    }
}

/// <summary>
/// <c>%LOCALAPPDATA%\AgentZeroLite\shortcut-settings.json</c> — same shape as the
/// other side-car stores.
/// </summary>
public static class ShortcutSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "shortcut-settings.json");

    public static ShortcutSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new ShortcutSettings();
            return JsonSerializer.Deserialize<ShortcutSettings>(File.ReadAllText(FilePath))
                   ?? new ShortcutSettings();
        }
        catch
        {
            return new ShortcutSettings();
        }
    }

    public static void Save(ShortcutSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
    }
}
