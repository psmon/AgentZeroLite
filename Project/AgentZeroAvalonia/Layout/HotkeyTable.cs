using Agent.Common.Services;
using AgentZeroAvalonia.Terminal;
using Avalonia.Input;

namespace AgentZeroAvalonia.Layout;

/// <summary>
/// The chords this host answers to (M0036). One table, two front doors: the renderer
/// matches it in JavaScript while it has focus (<c>hotkey</c> message), and the window
/// matches it in <see cref="Match(KeyEventArgs)"/> when focus is anywhere else.
///
/// Ids are the WPF host's <see cref="WindowCommandIds"/> plus a few this host needs;
/// bindings come from <see cref="ShortcutSettings"/> when the user enabled them, else
/// from the same suggested table the WPF settings page offers, plus host defaults for
/// the ids that table does not cover. On macOS Ctrl becomes Cmd.
/// </summary>
public static class HotkeyTable
{
    public const string NextTab = "terminal.next-tab";
    public const string PrevTab = "terminal.prev-tab";
    public const string MoveTabNextPane = "layout.move-tab-next-pane";
    public const string ClosePane = "layout.close-pane";
    public const string FocusLeft = "layout.focus-left";
    public const string FocusRight = "layout.focus-right";
    public const string FocusUp = "layout.focus-up";
    public const string FocusDown = "layout.focus-down";
    public const string BotToggle = "bot.toggle";

    /// <summary>Host defaults for ids the WPF suggested table does not bind.</summary>
    public static readonly IReadOnlyDictionary<string, string> HostDefaults = new Dictionary<string, string>
    {
        [WindowCommandIds.CloseTab] = "Ctrl+Alt+W",
        [NextTab] = "Ctrl+Alt+PageDown",
        [PrevTab] = "Ctrl+Alt+PageUp",
        [MoveTabNextPane] = "Ctrl+Alt+N",
        [ClosePane] = "Ctrl+Alt+Shift+W",
        [FocusLeft] = "Alt+Left",
        [FocusRight] = "Alt+Right",
        [FocusUp] = "Alt+Up",
        [FocusDown] = "Alt+Down",
        [BotToggle] = "Ctrl+Alt+B",
    };

    /// <summary>Ids this host implements; anything else in the settings is ignored here.</summary>
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>
    {
        WindowCommandIds.SplitRight, WindowCommandIds.SplitDown, WindowCommandIds.CloseTab,
        WindowCommandIds.TerminalAdd, WindowCommandIds.PanelToggle,
        NextTab, PrevTab, MoveTabNextPane, ClosePane, FocusLeft, FocusRight, FocusUp, FocusDown, BotToggle,
    };

    public static IReadOnlyList<HotkeyBinding> Build(ShortcutSettings? settings, bool isMac)
    {
        // User bindings first (they win a gesture), then the suggested table for whatever
        // they left unbound, then the host defaults for the ids the WPF table lacks.
        var bound = new Dictionary<string, ShortcutGesture>();
        if (settings is { Enabled: true })
        {
            foreach (var (gesture, id) in settings.ResolveBindings())
                if (Supported.Contains(id)) bound[id] = gesture;
        }
        foreach (var (id, text) in ShortcutSettings.Suggested)
            if (Supported.Contains(id) && !bound.ContainsKey(id) && ShortcutGesture.TryParse(text, out var g)) bound[id] = g;
        foreach (var (id, text) in HostDefaults)
            if (!bound.ContainsKey(id) && ShortcutGesture.TryParse(text, out var g)) bound[id] = g;

        // The first binding wins a gesture, as in ShortcutSettings.ResolveBindings.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<HotkeyBinding>();
        foreach (var (id, g) in bound)
        {
            if (!taken.Add(g.ToString())) continue;
            var ctrl = g.Modifiers.HasFlag(ShortcutModifiers.Control);
            var meta = g.Modifiers.HasFlag(ShortcutModifiers.Windows);
            if (isMac && ctrl) { ctrl = false; meta = true; }
            list.Add(new HotkeyBinding(id, g.Key, ctrl, g.Modifiers.HasFlag(ShortcutModifiers.Alt),
                g.Modifiers.HasFlag(ShortcutModifiers.Shift), meta));
        }
        return list;
    }

    /// <summary>Match an Avalonia key event against the table; null when nothing matches.</summary>
    public static string? Match(IReadOnlyList<HotkeyBinding> table, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        var ctrl = mods.HasFlag(KeyModifiers.Control);
        var alt = mods.HasFlag(KeyModifiers.Alt);
        var shift = mods.HasFlag(KeyModifiers.Shift);
        var meta = mods.HasFlag(KeyModifiers.Meta);
        var key = KeyName(e.Key);
        foreach (var h in table)
        {
            if (h.Ctrl != ctrl || h.Alt != alt || h.Shift != shift || h.Meta != meta) continue;
            if (string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase)) return h.Name;
        }
        return null;
    }

    /// <summary>Avalonia's key names, spelled the way <see cref="ShortcutGesture"/> spells them.</summary>
    private static string KeyName(Key key) => key switch
    {
        Key.Prior => "PageUp",
        Key.Next => "PageDown",
        Key.Return => "Enter",
        Key.OemPlus => "Plus",
        Key.OemMinus => "Minus",
        _ => key.ToString(),
    };
}
