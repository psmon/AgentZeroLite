using Agent.Common.Services;
using AgentZeroAvalonia.Layout;
using Xunit;

namespace AgentZeroAvalonia.Tests;

public class HotkeyTableTests
{
    [Fact]
    public void Defaults_cover_the_wpf_suggested_table_and_the_host_ids()
    {
        var table = HotkeyTable.Build(null, isMac: false);
        var split = Assert.Single(table, h => h.Name == WindowCommandIds.SplitRight);
        Assert.True(split.Ctrl && split.Alt && !split.Shift && !split.Meta);
        Assert.Equal("Right", split.Key);

        Assert.Contains(table, h => h.Name == HotkeyTable.FocusLeft && h.Alt && !h.Ctrl && h.Key == "Left");
        Assert.Contains(table, h => h.Name == HotkeyTable.NextTab && string.Equals(h.Key, "PageDown", StringComparison.OrdinalIgnoreCase));
        // Detach / dock: the WPF suggested chords, now that this host has floating terminals.
        Assert.Contains(table, h => h.Name == WindowCommandIds.Float && h.Ctrl && h.Alt && h.Key == "F");
        Assert.Contains(table, h => h.Name == WindowCommandIds.Dock && h.Ctrl && h.Alt && h.Key == "D");
    }

    [Fact]
    public void Find_is_ctrl_shift_f_never_plain_ctrl_f()
    {
        // Plain Ctrl+F is readline's forward-char inside every shell tab; the find chord must not take it.
        var find = Assert.Single(HotkeyTable.Build(null, isMac: false), h => h.Name == HotkeyTable.TerminalFind);
        Assert.True(find.Ctrl && find.Shift && !find.Alt && !find.Meta);
        Assert.Equal("F", find.Key);
        Assert.Contains(HotkeyTable.Commands, c => c.Id == HotkeyTable.TerminalFind);   // palette + Shortcuts page

        var mac = Assert.Single(HotkeyTable.Build(null, isMac: true), h => h.Name == HotkeyTable.TerminalFind);
        Assert.True(mac.Meta && mac.Shift && !mac.Ctrl);
    }

    [Fact]
    public void Mac_turns_ctrl_into_cmd()
    {
        var table = HotkeyTable.Build(null, isMac: true);
        var split = Assert.Single(table, h => h.Name == WindowCommandIds.SplitRight);
        Assert.False(split.Ctrl);
        Assert.True(split.Meta);
    }

    [Fact]
    public void User_bindings_win_and_a_gesture_is_bound_once()
    {
        var settings = new ShortcutSettings
        {
            Enabled = true,
            Bindings =
            {
                [WindowCommandIds.SplitRight] = "Ctrl+Shift+E",
                [WindowCommandIds.SplitDown] = "Ctrl+Shift+E",   // duplicate: the first keeps it
                [WindowCommandIds.Float] = "Ctrl+Shift+F",
                ["layout.teleport"] = "Ctrl+Shift+T",            // unknown to this host: ignored
            },
        };
        var table = HotkeyTable.Build(settings, isMac: false);
        var right = Assert.Single(table, h => h.Name == WindowCommandIds.SplitRight);
        Assert.Equal("E", right.Key);
        Assert.True(right.Ctrl && right.Shift);
        var down = Assert.Single(table, h => h.Name == WindowCommandIds.SplitDown);
        Assert.Equal("Down", down.Key); // fell back to the suggested table, not the clashing gesture
        var floatChord = Assert.Single(table, h => h.Name == WindowCommandIds.Float);
        Assert.True(floatChord.Ctrl && floatChord.Shift && floatChord.Key == "F");
        Assert.DoesNotContain(table, h => h.Name == "layout.teleport");
        Assert.Equal(table.Count, table.Select(h => (h.Key.ToLowerInvariant(), h.Ctrl, h.Alt, h.Shift, h.Meta)).Distinct().Count());
    }
}
