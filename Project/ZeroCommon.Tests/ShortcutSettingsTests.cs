using Agent.Common.Services;

namespace ZeroCommon.Tests;

/// <summary>
/// Keyboard shortcuts for the window commands. The rules worth pinning are the ones
/// that protect what is running <i>inside</i> a terminal: a shortcut that fires
/// while someone is typing takes a keystroke away from their CLI.
/// </summary>
public class ShortcutSettingsTests
{
    // ── the default ──

    [Fact]
    public void Disabled_ByDefault()
    {
        // The app shipped without shortcuts, so every combination currently belongs to
        // whatever runs in the terminal. Taking keys away is the user's call.
        Assert.False(new ShortcutSettings().Enabled);
        Assert.Empty(new ShortcutSettings().Bindings);
    }

    [Fact]
    public void Suggested_BindsOnlyKnownCommands()
    {
        Assert.NotEmpty(ShortcutSettings.Suggested);
        foreach (var (id, gesture) in ShortcutSettings.Suggested)
        {
            Assert.True(WindowCommandIds.IsKnown(id), $"unknown command id '{id}'");
            Assert.True(ShortcutGesture.TryParse(gesture, out _), $"unparseable gesture '{gesture}'");
        }
    }

    /// <summary>Ctrl+Shift+1/2/3 and Ctrl(+Shift)+` are already the bottom panel's.</summary>
    [Fact]
    public void Suggested_AvoidsTheShortcutsTheAppAlreadyUses()
    {
        var taken = new[] { "Ctrl+Shift+D1", "Ctrl+Shift+D2", "Ctrl+Shift+D3",
                            "Ctrl+Oemtilde", "Ctrl+Shift+Oemtilde" };
        foreach (var g in ShortcutSettings.Suggested.Values)
            Assert.DoesNotContain(g, taken);
    }

    // ── gesture parsing ──

    [Theory]
    [InlineData("Ctrl+Alt+Right", ShortcutModifiers.Control | ShortcutModifiers.Alt, "Right")]
    [InlineData("ctrl+shift+f", ShortcutModifiers.Control | ShortcutModifiers.Shift, "F")]
    [InlineData("Control+T", ShortcutModifiers.Control, "T")]
    [InlineData("Win+Alt+Down", ShortcutModifiers.Windows | ShortcutModifiers.Alt, "Down")]
    [InlineData("  Alt + Left  ", ShortcutModifiers.Alt, "Left")]
    public void TryParse_AcceptsTheCommonSpellings(string text, ShortcutModifiers mods, string key)
    {
        Assert.True(ShortcutGesture.TryParse(text, out var g));
        Assert.Equal(mods, g.Modifiers);
        Assert.Equal(key, g.Key);
    }

    /// <summary>The rule that keeps the terminal usable.</summary>
    [Theory]
    [InlineData("F")]
    [InlineData("Right")]
    [InlineData("F5")]
    public void TryParse_RejectsAnUnmodifiedKey(string text)
    {
        Assert.False(ShortcutGesture.TryParse(text, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt")]          // modifiers only
    [InlineData("Ctrl+A+B")]          // two keys is a typo, not a chord
    public void TryParse_RejectsNonsense(string? text)
    {
        Assert.False(ShortcutGesture.TryParse(text, out _));
    }

    [Fact]
    public void ToString_IsCanonical_SoTwoFilesThatAgreeLookAlike()
    {
        Assert.True(ShortcutGesture.TryParse("shift+alt+ctrl+right", out var a));
        Assert.True(ShortcutGesture.TryParse("Ctrl+Alt+Shift+Right", out var b));
        Assert.Equal(a.ToString(), b.ToString());
        Assert.Equal("Ctrl+Alt+Shift+Right", a.ToString());
    }

    // ── resolution ──

    [Fact]
    public void ResolveBindings_SkipsUnknownCommandsAndBadGestures()
    {
        var s = new ShortcutSettings
        {
            Bindings =
            {
                [WindowCommandIds.SplitRight] = "Ctrl+Alt+Right",
                ["layout.not-a-command"] = "Ctrl+Alt+X",
                [WindowCommandIds.SplitDown] = "nonsense",
                [WindowCommandIds.Float] = "",
            },
        };

        var resolved = s.ResolveBindings();

        Assert.Single(resolved);
        Assert.Equal(WindowCommandIds.SplitRight, resolved[0].CommandId);
    }

    /// <summary>Running both commands on one keypress would be worse than running one.</summary>
    [Fact]
    public void ResolveBindings_DuplicateKeys_FirstWins()
    {
        var s = new ShortcutSettings
        {
            Bindings =
            {
                [WindowCommandIds.SplitRight] = "Ctrl+Alt+S",
                [WindowCommandIds.SplitDown] = "ctrl+alt+s",   // same keys, other spelling
            },
        };

        Assert.Single(s.ResolveBindings());
        Assert.Equal(new[] { WindowCommandIds.SplitDown }, s.FindConflicts());
    }

    [Fact]
    public void FindConflicts_Clean_IsEmpty()
    {
        var s = new ShortcutSettings { Bindings = new Dictionary<string, string>(ShortcutSettings.Suggested) };
        Assert.Empty(s.FindConflicts());
        Assert.Equal(ShortcutSettings.Suggested.Count, s.ResolveBindings().Count);
    }

    // ── the command registry both front ends share ──

    [Fact]
    public void CommandIds_AreUniqueAndDescribed()
    {
        var ids = WindowCommandIds.All.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(WindowCommandIds.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
    }

    [Fact]
    public void IsKnown_IgnoresCase_AndRejectsStrangers()
    {
        Assert.True(WindowCommandIds.IsKnown(WindowCommandIds.SplitRight));
        Assert.True(WindowCommandIds.IsKnown("LAYOUT.SPLIT-RIGHT"));
        Assert.False(WindowCommandIds.IsKnown("layout.explode"));
        Assert.False(WindowCommandIds.IsKnown(null));
    }
}
