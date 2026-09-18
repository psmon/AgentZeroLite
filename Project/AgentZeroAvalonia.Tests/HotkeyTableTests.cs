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
        Assert.DoesNotContain(table, h => h.Name == WindowCommandIds.Float); // not implemented here
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
                [WindowCommandIds.Float] = "Ctrl+Shift+F",       // unsupported here: ignored
            },
        };
        var table = HotkeyTable.Build(settings, isMac: false);
        var right = Assert.Single(table, h => h.Name == WindowCommandIds.SplitRight);
        Assert.Equal("E", right.Key);
        Assert.True(right.Ctrl && right.Shift);
        var down = Assert.Single(table, h => h.Name == WindowCommandIds.SplitDown);
        Assert.Equal("Down", down.Key); // fell back to the suggested table, not the clashing gesture
        Assert.DoesNotContain(table, h => h.Name == WindowCommandIds.Float);
        Assert.Equal(table.Count, table.Select(h => (h.Key.ToLowerInvariant(), h.Ctrl, h.Alt, h.Shift, h.Meta)).Distinct().Count());
    }
}
