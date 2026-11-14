using System.Runtime.CompilerServices;
using Agent.Common.Services;

namespace AgentZeroAvalonia.Terminal;

/// <summary>
/// The size each terminal's renderer last fitted its pseudo-console to, keyed by session.
/// <see cref="ITerminalSession"/> (shared with the WPF host) carries no size, and a viewer that
/// draws the same output at another width — the Remote page's browser — wraps a TUI's frame into
/// garbage, so it sizes itself from here. Weak keys: a closed terminal's entry goes with it.
/// </summary>
internal static class TerminalGeometry
{
    private sealed class Box { public volatile int Cols, Rows; }

    private static readonly ConditionalWeakTable<ITerminalSession, Box> Sizes = new();

    public static void Set(ITerminalSession? session, int cols, int rows)
    {
        if (session is null || cols <= 0 || rows <= 0) return;
        var box = Sizes.GetOrCreateValue(session);
        box.Cols = cols;
        box.Rows = rows;
    }

    public static (int Cols, int Rows)? Get(ITerminalSession session)
        => Sizes.TryGetValue(session, out var box) && box.Cols > 0 ? (box.Cols, box.Rows) : null;
}
