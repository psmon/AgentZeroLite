using Termina.Terminal;

namespace AgentOne.Tui;

/// <summary>
/// The one-line input box is one line: a pasted paragraph must not push the
/// cursor off the screen or wrap into the transcript. This keeps a window of
/// the line around the cursor — about two thirds before it, a third after —
/// and marks a cut end with "…", so what you see is always where you are.
/// </summary>
public static class InputViewport
{
    public const string CursorGlyph = "▌";
    private const string Ellipsis = "…";

    /// <summary>
    /// The text to draw for <paramref name="text"/> with the cursor at
    /// <paramref name="cursor"/>, fitted into <paramref name="width"/> columns.
    /// </summary>
    public static string Render(string text, int cursor, int width)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);
        var left = text[..cursor];
        var right = text[cursor..];

        var available = Math.Max(4, width) - DisplayWidth.GetColumnCount(CursorGlyph);
        var leftColumns = DisplayWidth.GetColumnCount(left);
        var rightColumns = DisplayWidth.GetColumnCount(right);

        if (leftColumns + rightColumns <= available) return left + CursorGlyph + right;

        // Share the room: the part after the cursor gets a third at most, the
        // part before it takes the rest, and whatever one side does not need
        // goes back to the other.
        var rightWant = Math.Min(rightColumns, available / 3);
        var leftWant = Math.Min(leftColumns, available - rightWant);
        rightWant = Math.Min(rightColumns, available - leftWant);

        var leftCut = leftWant < leftColumns;
        var rightCut = rightWant < rightColumns;
        if (leftCut) leftWant -= DisplayWidth.GetColumnCount(Ellipsis);
        if (rightCut) rightWant -= DisplayWidth.GetColumnCount(Ellipsis);

        var leftShown = leftCut ? Ellipsis + DisplayWidth.TruncateStartToColumns(left, Math.Max(0, leftWant)) : left;
        var rightShown = rightCut ? DisplayWidth.TruncateToColumns(right, Math.Max(0, rightWant)) + Ellipsis : right;

        return leftShown + CursorGlyph + rightShown;
    }
}
