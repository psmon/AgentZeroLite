namespace AgentOne.Commands;

/// <param name="Kind">What ended the line.</param>
/// <param name="Text">What was typed, for <see cref="LineResult.Entered"/>.</param>
public readonly record struct LineResult(LineKind Kind, string Text = "")
{
    public static readonly LineResult ToggleMode = new(LineKind.ToggleMode);
    public static readonly LineResult EndOfInput = new(LineKind.EndOfInput);

    public static LineResult Entered(string text) => new(LineKind.Entered, text);
}

public enum LineKind
{
    Entered,
    /// <summary>Shift+Tab: switch between basic and smart.</summary>
    ToggleMode,
    /// <summary>Ctrl+D / Ctrl+C / EOF.</summary>
    EndOfInput
}

/// <summary>
/// Reads one line, and can also report Shift+Tab.
///
/// Console.ReadLine cannot: it hands back text and swallows everything else, so
/// a mode toggle bound to a key combination needs the keys themselves. That
/// costs the shell's line editing, so this puts back the parts a prompt
/// actually needs — insert, backspace, and moving within the line — and nothing
/// more. Redirected input skips all of it and reads a plain line, because a
/// pipe has no keys to press.
/// </summary>
public static class LineEditor
{
    public static LineResult Read(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            // Not Console.ReadLine: it decodes a redirected stream with the
            // console code page, which mangles anything non-ASCII.
            var piped = Services.StandardInput.ReadLine();
            return piped is null ? LineResult.EndOfInput : LineResult.Entered(piped);
        }

        Console.Write(prompt);

        var text = new System.Text.StringBuilder();
        var cursor = 0;

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return LineResult.Entered(text.ToString());

                case ConsoleKey.Tab when key.Modifiers.HasFlag(ConsoleModifiers.Shift):
                    // Leave the half-typed line on screen: the caller reprints
                    // the prompt in the new mode and hands the text back.
                    Console.WriteLine();
                    return new LineResult(LineKind.ToggleMode, text.ToString());

                case ConsoleKey.Tab:
                    continue;                       // nothing to complete

                case ConsoleKey.D when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    Console.WriteLine();
                    return LineResult.EndOfInput;

                case ConsoleKey.Backspace when cursor > 0:
                    text.Remove(cursor - 1, 1);
                    cursor--;
                    Redraw(prompt, text.ToString(), cursor);
                    continue;

                case ConsoleKey.Delete when cursor < text.Length:
                    text.Remove(cursor, 1);
                    Redraw(prompt, text.ToString(), cursor);
                    continue;

                case ConsoleKey.LeftArrow when cursor > 0:
                    cursor--;
                    Redraw(prompt, text.ToString(), cursor);
                    continue;

                case ConsoleKey.RightArrow when cursor < text.Length:
                    cursor++;
                    Redraw(prompt, text.ToString(), cursor);
                    continue;

                case ConsoleKey.Home:
                    cursor = 0;
                    Redraw(prompt, text.ToString(), cursor);
                    continue;

                case ConsoleKey.End:
                    cursor = text.Length;
                    Redraw(prompt, text.ToString(), cursor);
                    continue;

                case ConsoleKey.Escape:
                    text.Clear();
                    cursor = 0;
                    Redraw(prompt, "", 0);
                    continue;
            }

            if (char.IsControl(key.KeyChar) || key.KeyChar == '\0') continue;

            text.Insert(cursor, key.KeyChar);
            cursor++;
            Redraw(prompt, text.ToString(), cursor);
        }
    }

    /// <summary>
    /// Repaints the whole line. Simpler than patching it in place, and correct
    /// when a character is wider than one column — which Korean text always is.
    /// </summary>
    private static void Redraw(string prompt, string text, int cursor)
    {
        try
        {
            Console.Write('\r');
            Console.Write(new string(' ', Math.Min(Console.WindowWidth - 1, prompt.Length + text.Length + 8)));
            Console.Write('\r');
            Console.Write(prompt);
            Console.Write(text);

            // Walk back to the cursor rather than computing a column: the shell
            // knows how wide each glyph rendered, and we do not.
            for (int i = text.Length; i > cursor; i--) Console.Write('\b');
        }
        catch (IOException)
        {
            // No console to draw on; the text is still being collected.
        }
    }
}
