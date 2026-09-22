using System.Text;

namespace AgentOne.Agent;

/// <summary>
/// Turns the raw stream of an envelope into the answer, as it is written.
///
/// The model streams JSON — <c>{"tool":"final","args":{"text":"…"}}</c> — and
/// showing that verbatim is worse than showing nothing. This watches the
/// accumulating text, works out whether the envelope is a <c>final</c> at all,
/// and from then on emits only the decoded contents of its <c>text</c> field.
///
/// A tool call streams nothing: <c>grep</c> also has a <c>text</c> argument, and
/// printing a search pattern as though it were the answer would be a lie with a
/// very plausible shape.
/// </summary>
public sealed class FinalAnswerStreamer
{
    private enum State { SeekTool, SeekText, InText, Done, NotFinal }

    private readonly StringBuilder _raw = new();
    private State _state = State.SeekTool;
    private int _cursor;

    /// <summary>Everything emitted so far.</summary>
    public string Visible { get; private set; } = "";

    /// <summary>True once the envelope has been identified as a tool call, not an answer.</summary>
    public bool IsToolCall => _state == State.NotFinal;

    /// <summary>Feeds one fragment in, and returns whatever became visible because of it.</summary>
    public string Push(string delta)
    {
        if (_state is State.Done or State.NotFinal) return "";

        _raw.Append(delta);
        var buffer = _raw.ToString();
        var emitted = new StringBuilder();

        while (true)
        {
            switch (_state)
            {
                case State.SeekTool:
                    if (!TryReadStringValue(buffer, "tool", ref _cursor, out var tool)) return Flush(emitted);
                    if (!string.Equals(tool, ToolCall.FinalTool, StringComparison.OrdinalIgnoreCase))
                    {
                        _state = State.NotFinal;
                        return Flush(emitted);
                    }
                    _state = State.SeekText;
                    break;

                case State.SeekText:
                    if (!TryFindStringStart(buffer, "text", ref _cursor)) return Flush(emitted);
                    _state = State.InText;
                    break;

                case State.InText:
                    var more = ReadTextBody(buffer, ref _cursor, out var finished);
                    emitted.Append(more);
                    if (finished) _state = State.Done;
                    return Flush(emitted);

                default:
                    return Flush(emitted);
            }
        }
    }

    private string Flush(StringBuilder emitted)
    {
        var text = emitted.ToString();
        Visible += text;
        return text;
    }

    /// <summary>Finds <c>"key" : "value"</c> and returns the value, once all of it has arrived.</summary>
    private static bool TryReadStringValue(string buffer, string key, ref int cursor, out string value)
    {
        value = "";
        var start = cursor;
        if (!TryFindStringStart(buffer, key, ref start)) return false;

        var end = start;
        while (end < buffer.Length && buffer[end] != '"')
        {
            if (buffer[end] == '\\') end++;      // skip whatever is escaped
            end++;
        }

        if (end >= buffer.Length) return false;   // the value is still arriving

        value = buffer[start..end];
        cursor = end + 1;
        return true;
    }

    /// <summary>Moves the cursor just past the opening quote of <c>"key": "</c>.</summary>
    private static bool TryFindStringStart(string buffer, string key, ref int cursor)
    {
        var needle = "\"" + key + "\"";
        var at = buffer.IndexOf(needle, cursor, StringComparison.Ordinal);
        if (at < 0) return false;

        var i = at + needle.Length;
        while (i < buffer.Length && char.IsWhiteSpace(buffer[i])) i++;
        if (i >= buffer.Length || buffer[i] != ':') return false;

        i++;
        while (i < buffer.Length && char.IsWhiteSpace(buffer[i])) i++;
        if (i >= buffer.Length || buffer[i] != '"') return false;

        cursor = i + 1;
        return true;
    }

    /// <summary>
    /// Decodes as much of the string body as has arrived, stopping before a
    /// half-delivered escape so the next fragment can complete it.
    /// </summary>
    private static string ReadTextBody(string buffer, ref int cursor, out bool finished)
    {
        var text = new StringBuilder();
        finished = false;

        while (cursor < buffer.Length)
        {
            var c = buffer[cursor];

            if (c == '"')
            {
                cursor++;
                finished = true;
                break;
            }

            if (c != '\\')
            {
                text.Append(c);
                cursor++;
                continue;
            }

            // An escape needs at least one more character, \u four more.
            if (cursor + 1 >= buffer.Length) break;
            var escape = buffer[cursor + 1];

            if (escape == 'u')
            {
                if (cursor + 5 >= buffer.Length) break;
                if (ushort.TryParse(buffer.AsSpan(cursor + 2, 4), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var code))
                    text.Append((char)code);
                cursor += 6;
                continue;
            }

            text.Append(escape switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                'b' => '\b',
                'f' => '\f',
                _ => escape          // \" \\ \/ and anything else stands for itself
            });
            cursor += 2;
        }

        return text.ToString();
    }
}
