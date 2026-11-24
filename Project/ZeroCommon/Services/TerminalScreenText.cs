using System.Text;

namespace Agent.Common.Services;

/// <summary>
/// A screen snapshot as text for a model to read: escape codes out, the padding a terminal
/// row carries to its right edge removed, and runs of blank rows folded to one. The words and
/// their order are untouched.
/// </summary>
public static class TerminalScreenText
{
    public static string Tidy(string? screen)
    {
        if (string.IsNullOrEmpty(screen)) return "";
        var text = ApprovalParser.StripAnsiCodes(screen);
        var sb = new StringBuilder(text.Length);
        var blank = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                if (++blank > 1) continue;
            }
            else
            {
                blank = 0;
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString().Trim('\n');
    }
}
