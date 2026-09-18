using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Agent.Common.Services;
using AgentZeroWpf.Module;
// UseWindowsForms puts System.Windows.Forms in the global usings, so the WPF type has
// to be named. The rest of the clashes are already aliased in GlobalUsings.cs; this one
// is not, and CommandPaletteWindow names it per-file the same way.
using TextBox = System.Windows.Controls.TextBox;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// The Shortcuts tab — the front end for <see cref="ShortcutSettings"/>.
///
/// <para>The model shipped complete (a suggested keymap, duplicate detection, a store)
/// but nothing ever offered it, so the feature existed only for whoever knew to
/// hand-edit <c>shortcut-settings.json</c>. This is the missing half.</para>
///
/// <para>Rows are generated from <see cref="WindowCommandIds.All"/> rather than listed
/// in XAML, for the reason that registry exists: the CLI dispatches the same ids, and a
/// command reachable from one front end but not the other is the drift it was built to
/// prevent. Add a command there and it appears here.</para>
/// </summary>
public partial class SettingsPanel : UserControl
{
    private ShortcutSettings _shortcuts = new();
    private readonly Dictionary<string, TextBox> _shortcutBoxes = new(StringComparer.OrdinalIgnoreCase);
    private bool _shortcutsTabInit;

    private void InitializeShortcutsTab()
    {
        _shortcuts = ShortcutSettingsStore.Load();
        chkShortcutsEnabled.IsChecked = _shortcuts.Enabled;
        lblShortcutFilePath.Text = ShortcutSettingsStore.FilePath;

        BuildShortcutRows();
        RefreshShortcutStatus();

        _shortcutsTabInit = true;
    }

    private void BuildShortcutRows()
    {
        icShortcutRows.Items.Clear();
        _shortcutBoxes.Clear();

        foreach (var (id, description) in WindowCommandIds.All)
            icShortcutRows.Items.Add(BuildShortcutRow(id, description));
    }

    private UIElement BuildShortcutRow(string commandId, string description)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = Brush("#C8C8D8"),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });
        // The id too: it is what `-cli layout run` takes and what lands in the file.
        label.Children.Add(new TextBlock
        {
            Text = commandId,
            Foreground = Brush("#556677"),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 9,
        });
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var box = new TextBox
        {
            Style = (Style)Resources["DarkTextBox"],
            Width = 170,
            IsReadOnly = true,          // the keyboard writes this, not the caret
            IsReadOnlyCaretVisible = false,
            Cursor = Cursors.Hand,
            VerticalContentAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0),
            Tag = commandId,
            ToolTip = "Click, then press the combination. Backspace clears it.",
            Text = _shortcuts.Bindings.TryGetValue(commandId, out var existing) ? existing : "",
        };
        box.GotKeyboardFocus += OnShortcutBoxFocused;
        box.LostKeyboardFocus += OnShortcutBoxUnfocused;
        box.PreviewKeyDown += OnShortcutBoxKeyDown;
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        _shortcutBoxes[commandId] = box;

        var clear = new Button
        {
            Style = (Style)Resources["DarkButton"],
            Content = "✕",
            Padding = new Thickness(8, 2, 8, 2),
            Tag = commandId,
            ToolTip = "Unbind",
            VerticalAlignment = VerticalAlignment.Center,
        };
        clear.Click += OnClearOneShortcut;
        Grid.SetColumn(clear, 2);
        grid.Children.Add(clear);

        return grid;
    }

    // ── capture ──

    /// <summary>
    /// While a box has focus the window's own keymap must stand down, or a binding that
    /// already works could never be changed: MainWindow's handler is on
    /// <c>PreviewKeyDown</c>, which tunnels from the top, so Ctrl+Alt+Right would split
    /// the pane instead of being written down here.
    /// </summary>
    private void OnShortcutBoxFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        SetShortcutCapture(true);
        if (sender is TextBox box) box.BorderBrush = Brush("#00FFF0");
    }

    private void OnShortcutBoxUnfocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        SetShortcutCapture(false);
        if (sender is TextBox box) box.BorderBrush = Brush("#2A2A5E");
    }

    private static void SetShortcutCapture(bool active)
    {
        if (Application.Current?.MainWindow is AgentZeroWpf.UI.APP.MainWindow mw)
            mw.ShortcutCaptureActive = active;
    }

    private void OnShortcutBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.Tag is not string commandId) return;

        // Alt-combinations arrive as Key.System with the real key in SystemKey — the
        // same unwrapping MainWindow does when it matches one.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Tab keeps moving between boxes; without this the tab order is unreachable.
        if (key == Key.Tab) return;

        e.Handled = true;

        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            SetBinding(commandId, "");
            return;
        }

        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            Keyboard.ClearFocus();
            return;
        }

        // A modifier on its own is the first half of a combination, not a combination.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
                or Key.System or Key.None)
            return;

        var mods = ShortcutModifiers.None;
        var k = Keyboard.Modifiers;
        if (k.HasFlag(ModifierKeys.Control)) mods |= ShortcutModifiers.Control;
        if (k.HasFlag(ModifierKeys.Alt)) mods |= ShortcutModifiers.Alt;
        if (k.HasFlag(ModifierKeys.Shift)) mods |= ShortcutModifiers.Shift;
        if (k.HasFlag(ModifierKeys.Windows)) mods |= ShortcutModifiers.Windows;

        if (mods == ShortcutModifiers.None)
        {
            // The model would reject it anyway; say why rather than appear to ignore it.
            lblShortcutStatus.Text =
                "Needs a modifier — Ctrl, Alt, Shift or Win — so it cannot fire while you type.";
            lblShortcutStatus.Foreground = Brush("#FF2D95");
            return;
        }

        SetBinding(commandId, new ShortcutGesture(mods, key.ToString()).ToString());
    }

    // ── edits ──

    private void SetBinding(string commandId, string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) _shortcuts.Bindings.Remove(commandId);
        else _shortcuts.Bindings[commandId] = gesture;

        if (_shortcutBoxes.TryGetValue(commandId, out var box)) box.Text = gesture;
        SaveShortcuts();
    }

    private void OnClearOneShortcut(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string commandId }) SetBinding(commandId, "");
    }

    private void OnUseSuggestedShortcuts(object sender, RoutedEventArgs e)
    {
        // A starting point, not a merge: half a keymap someone half-remembers is worse
        // than one they can see the whole of.
        _shortcuts.Bindings = new Dictionary<string, string>(ShortcutSettings.Suggested);
        foreach (var (id, box) in _shortcutBoxes)
            box.Text = _shortcuts.Bindings.TryGetValue(id, out var g) ? g : "";
        SaveShortcuts();
    }

    private void OnClearAllShortcuts(object sender, RoutedEventArgs e)
    {
        _shortcuts.Bindings.Clear();
        foreach (var box in _shortcutBoxes.Values) box.Text = "";
        SaveShortcuts();
    }

    private void OnShortcutsEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_shortcutsTabInit) return;   // ignore the programmatic check during init
        _shortcuts.Enabled = chkShortcutsEnabled.IsChecked == true;
        SaveShortcuts();
    }

    // ── save ──

    private void SaveShortcuts()
    {
        ShortcutSettingsStore.Save(_shortcuts);

        // Live, not on restart: the window re-reads the keymap, and nothing about a
        // running terminal session is touched by it.
        (Application.Current?.MainWindow as AgentZeroWpf.UI.APP.MainWindow)?.ReloadShortcuts();

        RefreshShortcutStatus();
        AppLogger.Log($"[Settings] Shortcuts | enabled={_shortcuts.Enabled} " +
                      $"bound={_shortcuts.ResolveBindings().Count}");
    }

    private void RefreshShortcutStatus()
    {
        var conflicts = _shortcuts.FindConflicts();
        if (conflicts.Count > 0)
        {
            // Inert rather than broken — but invisible is worse than either.
            lblShortcutStatus.Text =
                $"Ignored — already bound to the same keys: {string.Join(", ", conflicts)}";
            lblShortcutStatus.Foreground = Brush("#FF2D95");
            return;
        }

        var bound = _shortcuts.ResolveBindings().Count;
        lblShortcutStatus.Text = _shortcuts.Enabled
            ? bound == 0
                ? "Enabled, but nothing is bound yet."
                : $"Saved — {bound} shortcut{(bound == 1 ? "" : "s")} active."
            : bound == 0
                ? "Saved."
                : $"Saved — {bound} bound, inactive until you enable shortcuts above.";
        lblShortcutStatus.Foreground = Brush("#556677");
    }

    private static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex)!);
}
