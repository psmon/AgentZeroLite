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

    /// <summary>Move the bot between the docked pane and its own window (M0041).</summary>
    public const string BotEmbedToggle = "bot.embed-toggle";

    /// <summary>Open / close the code view in the bottom panel (VS Code's explorer, read-only).</summary>
    public const string CodeToggle = "code.toggle";

    /// <summary>
    /// The command palette (M0042). Not WPF's Ctrl+J: inside a terminal Ctrl+J is a line feed,
    /// and Claude Code's newline key — a palette there would take it from every agent tab.
    /// </summary>
    public const string PaletteOpen = "palette.open";

    /// <summary>
    /// Find in the active terminal's scrollback (xterm search addon). Ctrl+Shift+F, not Ctrl+F:
    /// inside a shell Ctrl+F is readline's forward-char, and a TUI may bind it too.
    /// </summary>
    public const string TerminalFind = "terminal.find";

    /// <summary>A binding value that removes a default rather than leaving it in place.</summary>
    public const string Unbound = "none";

    /// <summary>Every command a chord can run in this host, with what it does — the Shortcuts page and the palette list these.</summary>
    public static readonly IReadOnlyList<(string Id, string Description)> Commands = new[]
    {
        (PaletteOpen, "Open the command palette"),
        (WindowCommandIds.TerminalAdd, "Add a terminal to the active workspace"),
        (WindowCommandIds.CloseTab, "Close the active terminal tab"),
        (TerminalFind, "Find in the active terminal"),
        (NextTab, "Next terminal tab"),
        (PrevTab, "Previous terminal tab"),
        (WindowCommandIds.SplitRight, "Split the active terminal to the right"),
        (WindowCommandIds.SplitDown, "Split the active terminal downwards"),
        (MoveTabNextPane, "Move the active tab to the next pane"),
        (ClosePane, "Close the active pane"),
        (FocusLeft, "Focus the pane to the left"),
        (FocusRight, "Focus the pane to the right"),
        (FocusUp, "Focus the pane above"),
        (FocusDown, "Focus the pane below"),
        (WindowCommandIds.Float, "Detach the active terminal into its own window"),
        (WindowCommandIds.Dock, "Dock the active terminal back into the main window"),
        (WindowCommandIds.PanelToggle, "Show or hide the bot (the WPF panel key)"),
        (BotToggle, "Show or hide the bot"),
        (BotEmbedToggle, "Move the bot between the dock and its own window"),
        (CodeToggle, "Open or close the code view"),
    };

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
        [BotEmbedToggle] = "Ctrl+Shift+Backquote",
        [CodeToggle] = "Ctrl+Alt+E",
        [PaletteOpen] = "Ctrl+Shift+P",
        [TerminalFind] = "Ctrl+Shift+F",
    };

    /// <summary>Ids this host implements; anything else in the settings is ignored here.</summary>
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>
    {
        WindowCommandIds.SplitRight, WindowCommandIds.SplitDown, WindowCommandIds.CloseTab,
        WindowCommandIds.TerminalAdd, WindowCommandIds.PanelToggle,
        WindowCommandIds.Float, WindowCommandIds.Dock,
        NextTab, PrevTab, MoveTabNextPane, ClosePane, FocusLeft, FocusRight, FocusUp, FocusDown, BotToggle,
        BotEmbedToggle, CodeToggle, PaletteOpen, TerminalFind,
    };

    /// <summary>The binding an id has with no user setting: the WPF suggested table, else this host's default.</summary>
    public static string? DefaultGesture(string id)
        => ShortcutSettings.Suggested.TryGetValue(id, out var s) ? s : HostDefaults.TryGetValue(id, out var h) ? h : null;

    /// <summary>
    /// The gesture each supported id ends up with — what <see cref="Build"/> binds, as text, before
    /// the Ctrl→Cmd swap. An id that lost its keys to an earlier one, or was set to
    /// <see cref="Unbound"/>, is absent.
    /// </summary>
    public static IReadOnlyDictionary<string, ShortcutGesture> Effective(ShortcutSettings? settings)
    {
        // User bindings first (they win a gesture), then the suggested table for whatever
        // they left unbound, then the host defaults for the ids the WPF table lacks. User
        // bindings are read for every id this host runs, not only WPF's WindowCommandIds,
        // so the host's own commands can be rebound too; "none" removes a default.
        var bound = new Dictionary<string, ShortcutGesture>();
        var removed = new HashSet<string>();
        if (settings is { Enabled: true })
        {
            // A user gesture already claimed by an earlier user binding is dropped (the id then
            // keeps its default), as ShortcutSettings.ResolveBindings does.
            var userTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, text) in settings.Bindings)
            {
                if (!Supported.Contains(id)) continue;
                if (string.Equals(text?.Trim(), Unbound, StringComparison.OrdinalIgnoreCase)) { removed.Add(id); continue; }
                if (!bound.ContainsKey(id) && ShortcutGesture.TryParse(text, out var g) && userTaken.Add(g.ToString())) bound[id] = g;
            }
        }
        foreach (var (id, _) in Commands)   // a fixed order, so which default wins a clash never varies
            if (!bound.ContainsKey(id) && !removed.Contains(id) && ShortcutGesture.TryParse(DefaultGesture(id), out var g)) bound[id] = g;

        // The first binding wins a gesture, as in ShortcutSettings.ResolveBindings.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, ShortcutGesture>();
        foreach (var (id, g) in bound)
            if (taken.Add(g.ToString())) result[id] = g;
        return result;
    }

    /// <summary>The gesture as the user will press it on this OS (Ctrl reads Cmd on macOS).</summary>
    public static string Display(ShortcutGesture g, bool isMac)
        => isMac ? g.ToString().Replace("Ctrl+", "Cmd+") : g.ToString();

    public static IReadOnlyList<HotkeyBinding> Build(ShortcutSettings? settings, bool isMac)
    {
        var list = new List<HotkeyBinding>();
        foreach (var (id, g) in Effective(settings))
        {
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
        // Spelled the way the renderer reports it. term.js matches e.key OR e.code, and with
        // Shift held e.key is "~" - only e.code === "Backquote" matches. Spelling this
        // "OemTilde" would work in the window and fail silently inside a focused terminal.
        Key.OemTilde => "Backquote",
        _ => key.ToString(),
    };
}
