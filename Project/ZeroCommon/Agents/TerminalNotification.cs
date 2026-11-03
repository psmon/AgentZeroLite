using System.Text;
using System.Text.RegularExpressions;

namespace Agent.Common.Agents;

/// <summary>A desktop notification a program in a terminal asked for with an OSC sequence.</summary>
/// <param name="Title">Short title, or empty.</param>
/// <param name="Body">The message, or empty. At least one of the two is non-empty.</param>
/// <param name="Source">Which sequence carried it: <c>osc9</c>, <c>osc777</c> or <c>osc99</c>.</param>
public sealed record TerminalNotification(string Title, string Body, string Source)
{
    /// <summary>One line for the popup: "title — body", or whichever exists.</summary>
    public string Text => Title.Length == 0 ? Body : Body.Length == 0 ? Title : $"{Title} — {Body}";
}

/// <summary>
/// Turns the notification escape sequences terminal programs emit into
/// <see cref="TerminalNotification"/>s, so any CLI agent — not only the three whose state
/// AgentZero reads from files — can say "I need you" (cmux / Warp / Ghostty all honour these):
///
/// <list type="bullet">
/// <item><c>OSC 9 ; text</c> — iTerm2's notification. ConEmu reuses OSC 9 for numbered
/// commands (<c>9;4;st;pr</c> is the progress bar Windows Terminal and winget emit), so data
/// that starts with a number and then <c>;</c> or ends is a command, not a message.</item>
/// <item><c>OSC 777 ; notify ; title ; body</c> — rxvt-unicode / foot / Ghostty.</item>
/// <item><c>OSC 99 ; metadata ; payload</c> — kitty. Metadata is <c>k=v</c> pairs split by
/// <c>:</c>; <c>p=title|body</c> says which part, <c>e=1</c> means base64. Chunked messages
/// (<c>d=0</c>) are delivered per chunk — the simple case agents use is a single chunk.</item>
/// </list>
///
/// The text comes from whatever runs in the tab, so it is treated as untrusted display text:
/// control characters are dropped and the length is capped.
/// </summary>
public static partial class TerminalNotificationParser
{
    public const int MaxLength = 240;

    [GeneratedRegex(@"^\d+(;|$)")]
    private static partial Regex ConEmuCommand();

    /// <param name="osc">The OSC number (9, 777 or 99).</param>
    /// <param name="data">Everything after <c>OSC n ;</c>, up to the terminator.</param>
    public static TerminalNotification? Parse(int osc, string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        return osc switch
        {
            9 => ParseOsc9(data),
            777 => ParseOsc777(data),
            99 => ParseOsc99(data),
            _ => null,
        };
    }

    private static TerminalNotification? ParseOsc9(string data)
    {
        if (ConEmuCommand().IsMatch(data)) return null;
        return Make("", data, "osc9");
    }

    private static TerminalNotification? ParseOsc777(string data)
    {
        var parts = data.Split(';', 3);
        if (parts.Length < 2 || !parts[0].Equals("notify", StringComparison.OrdinalIgnoreCase)) return null;
        return Make(parts[1], parts.Length > 2 ? parts[2] : "", "osc777");
    }

    private static TerminalNotification? ParseOsc99(string data)
    {
        var split = data.IndexOf(';');
        if (split < 0) return null;
        var meta = data[..split];
        var payload = data[(split + 1)..];
        string part = "title";
        var base64 = false;
        foreach (var kv in meta.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = kv.IndexOf('=');
            if (eq <= 0) continue;
            var key = kv[..eq];
            var value = kv[(eq + 1)..];
            if (key == "p") part = value;
            else if (key == "e") base64 = value == "1";
        }
        // Queries (p=?), icons, buttons, close requests: not a message to show.
        if (part is not ("title" or "body")) return null;
        if (base64)
        {
            try { payload = Encoding.UTF8.GetString(Convert.FromBase64String(payload)); }
            catch (FormatException) { return null; }
        }
        return part == "title" ? Make(payload, "", "osc99") : Make("", payload, "osc99");
    }

    private static TerminalNotification? Make(string title, string body, string source)
    {
        title = Clean(title);
        body = Clean(body);
        if (title.Length == 0 && body.Length == 0) return null;
        return new TerminalNotification(title, body, source);
    }

    /// <summary>Drop control characters (incl. a stray ESC or BEL), collapse whitespace, cap the length.</summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, MaxLength + 1));
        var space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))   // newline / tab / space → one space
            {
                if (!space && sb.Length > 0) { sb.Append(' '); space = true; }
                continue;
            }
            if (char.IsControl(c)) continue;   // ESC, BEL and the rest vanish
            sb.Append(c);
            space = false;
            if (sb.Length > MaxLength) break;
        }
        var text = sb.ToString().TrimEnd();
        return text.Length > MaxLength ? text[..MaxLength].TrimEnd() + "…" : text;
    }
}
