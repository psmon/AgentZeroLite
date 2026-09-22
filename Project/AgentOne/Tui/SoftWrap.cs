using System.Text;
using Termina.Terminal;

namespace AgentOne.Tui;

/// <summary>
/// Folds text into lines no wider than the window before it reaches the
/// transcript buffer. Termina's StreamingTextNode wraps long lines itself, but
/// it re-measures the whole line for every cell it draws, so one 2,300-character
/// answer line froze the window for nine seconds (a 4,600-character one for over
/// a minute) — measured with a stack dump in <c>StreamingTextNode.Render →
/// DisplayWidth.GetColumnCount</c>. Short lines make that cost vanish, and
/// folding at word boundaries here also keeps continuation lines readable.
/// Keeps its column across fragments, so streamed deltas fold as one line.
/// </summary>
public sealed class SoftWrap
{
    /// <summary>Narrower than this and folding is pointless.</summary>
    public const int MinWidth = 8;

    /// <summary>Display column the next fragment lands on.</summary>
    public int Column { get; private set; }

    /// <summary>Starts a new line without emitting one (after an AppendLine).</summary>
    public void NewLine() => Column = 0;

    /// <summary>
    /// Returns <paramref name="text"/> with newlines inserted wherever the
    /// current line would otherwise exceed <paramref name="width"/> columns.
    /// Breaks before a word when it can, inside one only when the word alone
    /// is wider than the line. Whitespace that lands on a fold is dropped.
    /// </summary>
    public string Fold(string text, int width)
    {
        if (text.Length == 0) return text;
        if (width < MinWidth) width = MinWidth;

        var sb = new StringBuilder(text.Length + 16);
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\n')
            {
                sb.Append('\n');
                Column = 0;
                i++;
                continue;
            }

            if (c == '\r') { i++; continue; }

            // One token: a run of whitespace, or a run of anything else.
            var space = char.IsWhiteSpace(c);
            var j = i;
            while (j < text.Length && text[j] is not ('\n' or '\r') && char.IsWhiteSpace(text[j]) == space) j++;
            var token = text[i..j];
            i = j;

            var columns = DisplayWidth.GetColumnCount(token);

            if (space)
            {
                if (Column + columns > width)
                {
                    // The gap is where the line folds; nothing of it survives.
                    sb.Append('\n');
                    Column = 0;
                }
                else
                {
                    sb.Append(token);
                    Column += columns;
                }
                continue;
            }

            if (Column > 0 && Column + columns > width)
            {
                TrimLineEnd(sb);
                sb.Append('\n');
                Column = 0;
            }

            // A word wider than the line (a URL, a hash): break it by columns.
            while (columns > width)
            {
                var head = DisplayWidth.TruncateToColumns(token, width);
                if (head.Length == 0) break;
                sb.Append(head).Append('\n');
                token = token[head.Length..];
                columns = DisplayWidth.GetColumnCount(token);
                Column = 0;
            }

            sb.Append(token);
            Column += columns;
        }

        return sb.ToString();
    }

    /// <summary>Whitespace before a fold would sit at the end of the line; drop it.</summary>
    private static void TrimLineEnd(StringBuilder sb)
    {
        var end = sb.Length;
        while (end > 0 && sb[end - 1] != '\n' && char.IsWhiteSpace(sb[end - 1])) end--;
        sb.Length = end;
    }
}
