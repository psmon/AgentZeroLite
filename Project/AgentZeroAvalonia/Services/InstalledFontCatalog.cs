using Agent.Common;
using Avalonia.Media;

namespace AgentZeroAvalonia.Services;

/// <summary>One font family the Settings picker can offer.</summary>
/// <param name="Name">The family name as CSS should see it (and as the stack stores it).</param>
/// <param name="IsMonospace">Every Latin glyph has the same advance — what a terminal grid needs.</param>
/// <param name="IsBundled">Shipped in <c>Wasm/xterm/vendor/fonts</c>, so it renders even when not installed.</param>
public sealed record InstalledFont(string Name, bool IsMonospace, bool IsBundled)
{
    /// <summary>The face the picker draws the name in, so the list is its own preview.</summary>
    public FontFamily Family { get; } = FontFamilyOrDefault(Name);

    public override string ToString() => Name;

    private static FontFamily FontFamilyOrDefault(string name)
    {
        try { return new FontFamily(name); } catch { return FontFamily.Default; }
    }
}

/// <summary>
/// The font families on this machine, read once per process through Avalonia's
/// <see cref="FontManager"/> (DirectWrite / CoreText / fontconfig underneath, so the same
/// code answers on every host). The terminal is xterm.js in a web view, which resolves a
/// family name through the same system font collection, so a name listed here is a name
/// the renderer can find.
/// </summary>
public static class InstalledFontCatalog
{
    /// <summary>Ships with the app (OFL 1.1) — offered even where it is not installed.</summary>
    public const string BundledFamily = "JetBrains Mono";

    private static Task<IReadOnlyList<InstalledFont>>? _load;
    private static readonly object Gate = new();

    /// <summary>All families, sorted by name; empty when no font manager is available (headless tests).</summary>
    public static Task<IReadOnlyList<InstalledFont>> LoadAsync()
    {
        lock (Gate)
            return _load ??= Task.Run(Read);
    }

    private static IReadOnlyList<InstalledFont> Read()
    {
        var fonts = new Dictionary<string, InstalledFont>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var manager = FontManager.Current;
            foreach (var family in manager.SystemFonts)
            {
                var name = family.Name;
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith('@') || fonts.ContainsKey(name)) continue;
                fonts[name] = new InstalledFont(name, IsMonospace(manager, family), IsBundled: false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Settings] installed fonts unavailable | {ex.GetType().Name}: {ex.Message}");
        }

        var installed = fonts.Count;
        fonts[BundledFamily] = new InstalledFont(BundledFamily, IsMonospace: true, IsBundled: true);
        AppLogger.Log($"[Settings] installed fonts | {installed} families, {fonts.Values.Count(f => f.IsMonospace)} monospace (incl. bundled {BundledFamily})");
        return fonts.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // A fixed-pitch flag alone is not enough: plenty of coding fonts (and most CJK
    // "coding" fonts, whose Hangul is double width) leave it unset. Comparing the advances
    // of a narrow and a wide Latin glyph answers the question a terminal actually asks.
    private static bool IsMonospace(FontManager manager, FontFamily family)
    {
        try
        {
            if (!manager.TryGetGlyphTypeface(new Typeface(family), out var face)) return false;
            if (face.Metrics.IsFixedPitch) return true;

            var map = face.CharacterToGlyphMap;
            double? width = null;
            foreach (var c in "iWm0.")
            {
                if (!map.TryGetGlyph(c, out var glyph) || glyph == 0) return false;
                if (!face.TryGetHorizontalGlyphAdvance(glyph, out var advance)) return false;
                if (width is null) width = advance;
                else if (Math.Abs(width.Value - advance) > 0.5) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
