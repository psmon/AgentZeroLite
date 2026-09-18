using System.Text;

namespace Agent.Common.Services;

/// <summary>
/// Splits a stored <c>Arguments</c> string into argv for hosts that spawn with an
/// argument list rather than a command line (Porta.Pty on macOS, M0033). Double and
/// single quotes group; a backslash inside double quotes escapes the next character.
/// Windows keeps passing the raw command line to ConPTY, so this is only ever the
/// POSIX reading of the same field.
/// </summary>
public static class CommandLineSplitter
{
    public static string[] Split(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        var args = new List<string>();
        var current = new StringBuilder();
        var inArg = false;
        char? quote = null;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (quote is { } q)
            {
                if (c == q)
                {
                    quote = null;
                }
                else if (q == '"' && c == '\\' && i + 1 < commandLine.Length && (commandLine[i + 1] == '"' || commandLine[i + 1] == '\\'))
                {
                    current.Append(commandLine[++i]);
                }
                else
                {
                    current.Append(c);
                }
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
                inArg = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inArg)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    inArg = false;
                }
            }
            else
            {
                current.Append(c);
                inArg = true;
            }
        }
        if (inArg) args.Add(current.ToString());
        return args.ToArray();
    }

    /// <summary>The inverse, for the Windows command line: quote what contains spaces or quotes.</summary>
    public static string Join(IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        foreach (var a in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (a.Length > 0 && a.IndexOfAny([' ', '\t', '"']) < 0) sb.Append(a);
            else sb.Append('"').Append(a.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
        }
        return sb.ToString();
    }
}
