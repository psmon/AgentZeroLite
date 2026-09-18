using Avalonia.Input;
using AgentZeroAvalonia.Layout;
using Xunit;

namespace AgentZeroAvalonia.Tests;

/// <summary>
/// M0041 — the chord that moves the bot between the shell and its own window, and the key
/// spelling that makes it work while a terminal has focus.
/// </summary>
public class BotEmbedHotkeyTests
{
    [Fact]
    public void The_embed_toggle_is_bound_and_becomes_cmd_on_mac()
    {
        var windows = HotkeyTable.Build(null, isMac: false);
        var mac = HotkeyTable.Build(null, isMac: true);

        var win = Assert.Single(windows, b => b.Name == HotkeyTable.BotEmbedToggle);
        Assert.True(win.Ctrl);
        Assert.True(win.Shift);
        Assert.False(win.Meta);

        var osx = Assert.Single(mac, b => b.Name == HotkeyTable.BotEmbedToggle);
        Assert.True(osx.Meta);
        Assert.False(osx.Ctrl);
    }

    /// <summary>
    /// The renderer reports the key by <c>e.code</c> when Shift is held (<c>e.key</c> is "~"),
    /// so the table has to spell this one "Backquote". Getting it wrong works in the window
    /// and fails silently inside a focused terminal — the place the chord is actually used.
    /// </summary>
    [Fact]
    public void The_backquote_key_is_named_the_way_the_renderer_reports_it()
    {
        var binding = Assert.Single(
            HotkeyTable.Build(null, isMac: false),
            b => b.Name == HotkeyTable.BotEmbedToggle);

        Assert.Equal("Backquote", binding.Key, ignoreCase: true);

        // And the window path agrees: OemTilde must match that same name.
        var table = HotkeyTable.Build(null, isMac: false);
        var matched = HotkeyTable.Match(table, new KeyEventArgs
        {
            Key = Key.OemTilde,
            KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift,
        });
        Assert.Equal(HotkeyTable.BotEmbedToggle, matched);
    }

    [Fact]
    public void The_embed_toggle_is_a_supported_id()
        => Assert.Contains(HotkeyTable.BotEmbedToggle, HotkeyTable.Supported);
}
