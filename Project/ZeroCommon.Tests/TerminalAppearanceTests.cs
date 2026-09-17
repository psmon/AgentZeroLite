using Agent.Common.Services;

namespace ZeroCommon.Tests;

/// <summary>
/// Terminal appearance resolution — the part of Settings that has to be right
/// before anything reaches the renderer: a bad font stack mis-measures every
/// cell, and a theme name that silently resolves to nothing throws away a
/// hand-edited palette.
/// </summary>
public class TerminalAppearanceTests
{
    [Fact]
    public void Defaults_AreTheShippedFontAndTheme()
    {
        var s = new TerminalSettings();

        Assert.StartsWith("JetBrains Mono", s.EffectiveFontFamily);
        Assert.Equal(14, s.EffectiveFontSize);
        Assert.Equal(TerminalThemeCatalog.DefaultName, s.ThemeName);
        Assert.Equal(TerminalBackend.WebViewXterm, s.EffectiveBackend);
    }

    /// <summary>One terminal: a settings file naming the old backend still loads,
    /// and still gets the one that exists.</summary>
    [Theory]
    [InlineData(TerminalBackend.EasyConPty)]
    [InlineData(TerminalBackend.WebViewXterm)]
    public void EffectiveBackend_IsAlwaysTheOneBackend(TerminalBackend stored)
    {
        Assert.Equal(TerminalBackend.WebViewXterm,
            new TerminalSettings { Backend = stored }.EffectiveBackend);
    }

    // ── font ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EffectiveFontFamily_NeverEmpty(string? stack)
    {
        // An empty stack leaves xterm.js measuring a font that does not exist.
        Assert.Equal("monospace", new TerminalSettings { FontFamily = stack! }.EffectiveFontFamily);
    }

    [Fact]
    public void EffectiveFontFamily_KeepsAUserStackVerbatim()
    {
        var s = new TerminalSettings { FontFamily = "  D2Coding, monospace  " };
        Assert.Equal("D2Coding, monospace", s.EffectiveFontFamily);
    }

    [Theory]
    [InlineData(0, TerminalSettings.MinFontSize)]
    [InlineData(-5, TerminalSettings.MinFontSize)]
    [InlineData(999, TerminalSettings.MaxFontSize)]
    [InlineData(16, 16)]
    public void EffectiveFontSize_IsClamped(int stored, int expected)
    {
        Assert.Equal(expected, new TerminalSettings { FontSize = stored }.EffectiveFontSize);
    }

    [Theory]
    [InlineData(0.0, 0.8)]
    [InlineData(5.0, 2.0)]
    [InlineData(1.2, 1.2)]
    public void EffectiveLineHeight_IsClamped(double stored, double expected)
    {
        Assert.Equal(expected, new TerminalSettings { LineHeight = stored }.EffectiveLineHeight, 3);
    }

    // ── theme ──

    [Fact]
    public void Catalog_ListsPresetsAndCustomLast()
    {
        Assert.Contains(TerminalThemeCatalog.DefaultName, TerminalThemeCatalog.Names);
        Assert.Equal(TerminalThemeCatalog.CustomName, TerminalThemeCatalog.Names[^1]);
        Assert.True(TerminalThemeCatalog.Names.Count >= 5);
    }

    [Fact]
    public void Catalog_EveryPreset_FillsAllTwentySlots()
    {
        foreach (var name in TerminalThemeCatalog.Names)
        {
            var theme = TerminalThemeCatalog.Get(name);
            if (theme is null) continue;   // Custom

            foreach (var (slot, value) in Slots(theme))
            {
                Assert.True(value is { Length: 7 } && value[0] == '#',
                    $"{name}.{slot} should be a #rrggbb colour, was '{value}'");
            }
        }
    }

    [Fact]
    public void Catalog_PresetsAreDistinct()
    {
        var backgrounds = TerminalThemeCatalog.Names
            .Select(TerminalThemeCatalog.Get)
            .Where(t => t is not null)
            .Select(t => t!.Background)
            .ToList();

        Assert.Equal(backgrounds.Count, backgrounds.Distinct().Count());
    }

    [Fact]
    public void EffectiveTheme_ResolvesAPresetByName_IgnoringCase()
    {
        var s = new TerminalSettings { ThemeName = "tokyo night" };
        Assert.Equal("#1a1b26", s.EffectiveTheme.Background);
    }

    /// <summary>The reason presets resolve on read instead of being written into the
    /// file: a palette someone edited by hand survives a trip through the dropdown.</summary>
    [Fact]
    public void EffectiveTheme_Custom_UsesTheStoredPalette()
    {
        var s = new TerminalSettings
        {
            ThemeName = TerminalThemeCatalog.CustomName,
            Theme = new TerminalTheme { Background = "#123456" },
        };
        Assert.Equal("#123456", s.EffectiveTheme.Background);
    }

    [Fact]
    public void EffectiveTheme_UnknownName_FallsBackToTheStoredPalette()
    {
        var s = new TerminalSettings
        {
            ThemeName = "Theme From A Newer Build",
            Theme = new TerminalTheme { Background = "#abcdef" },
        };
        Assert.Equal("#abcdef", s.EffectiveTheme.Background);
    }

    private static IEnumerable<(string Slot, string Value)> Slots(TerminalTheme t) =>
        typeof(TerminalTheme).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => (p.Name, (string)p.GetValue(t)!));
}
