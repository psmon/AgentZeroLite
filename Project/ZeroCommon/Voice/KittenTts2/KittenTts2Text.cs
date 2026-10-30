using System.Text.RegularExpressions;

namespace Agent.Common.Voice.KittenTts2;

/// <summary>
/// kittenml's chunking and joining (text.py): long text goes to the model as sentence-packed chunks of at most
/// 380 characters, chunks under 130 fold into a neighbour (short prompts are the unstable ones), every chunk ends in
/// a mark; the chunks' audio is trimmed of edge silence, faded 8 ms at each edge and joined with 0.16 s pauses.
/// </summary>
public static partial class KittenTts2Text
{
    public const int ChunkChars = 380;
    public const int ChunkMinChars = 130;
    public const double ChunkGapSeconds = 0.16;
    private const string Terminal = ".!?,;:";

    [GeneratedRegex(@"(?<=[.!?])[""’”')\]]*\s+")]
    private static partial Regex SentenceEnd();

    public static List<string> Split(string text, int maxChars = ChunkChars, int minChars = ChunkMinChars)
    {
        var stripped = (text ?? "").Trim();
        if (maxChars <= 0 || stripped.Length <= maxChars) return stripped.Length > 0 ? [stripped] : [];
        var output = new List<string>();
        var current = "";
        foreach (var raw in SentenceEnd().Split(stripped).Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var sentence = raw.Trim();
            while (sentence.Length > maxChars)
            {
                var cut = Math.Max(Math.Max(LastIndex(sentence, ", ", maxChars), LastIndex(sentence, "; ", maxChars)),
                    LastIndex(sentence, " — ", maxChars));
                cut = cut > maxChars / 3 ? cut + 1 : maxChars;
                var head = sentence[..cut].Trim();
                sentence = sentence[cut..].Trim();
                if (current.Length > 0)
                {
                    output.Add(current);
                    current = "";
                }
                output.Add(head);
            }
            if (current.Length == 0) current = sentence;
            else if (current.Length + 1 + sentence.Length <= maxChars) current += " " + sentence;
            else
            {
                output.Add(current);
                current = sentence;
            }
        }
        if (current.Length > 0) output.Add(current);
        return MergeShort(output, maxChars, minChars).Select(EnsureTerminal).ToList();
    }

    // Python: str.rfind(sub, 0, end) - the match must end at or before `end`. .NET's LastIndexOf(value, startIndex)
    // searches s[0..startIndex] inclusive, so the match's last character must sit at end - 1 or before.
    internal static int LastIndex(string s, string sub, int end)
    {
        var last = Math.Min(end, s.Length) - 1;
        return last < 0 ? -1 : s.LastIndexOf(sub, last, StringComparison.Ordinal);
    }

    private static List<string> MergeShort(List<string> parts, int maxChars, int minChars)
    {
        if (parts.Count == 0 || minChars <= 0) return parts;
        var output = new List<string>(parts);
        var limit = (int)(maxChars * 1.4);
        var changed = true;
        while (changed && output.Count > 1)
        {
            changed = false;
            for (var i = 0; i < output.Count; i++)
            {
                var chunk = output[i];
                if (chunk.Length >= minChars) continue;
                var prevOk = i > 0 && output[i - 1].Length + 1 + chunk.Length <= limit;
                var nextOk = i < output.Count - 1 && chunk.Length + 1 + output[i + 1].Length <= limit;
                if (prevOk && (!nextOk || output[i - 1].Length <= output[i + 1].Length))
                {
                    output[i - 1] = output[i - 1].TrimEnd() + " " + chunk.TrimStart();
                    output.RemoveAt(i);
                }
                else if (nextOk)
                {
                    output[i] = chunk.TrimEnd() + " " + output[i + 1].TrimStart();
                    output.RemoveAt(i + 1);
                }
                else continue;
                changed = true;
                break;
            }
        }
        return output;
    }

    private static string EnsureTerminal(string chunk)
    {
        var c = (chunk ?? "").TrimEnd();
        if (c.Length == 0) return c;
        return Terminal.Contains(c[^1]) || "\"’”')]".Contains(c[^1]) ? c : c + ",";
    }

    /// <summary>Trim a segment's lead-in and trail-out below -45 dB (10 ms frames), keeping 20 ms.</summary>
    public static float[] TrimSilence(float[] wav, int rate, double threshDb = -45.0, double keepSeconds = 0.02)
    {
        if (wav.Length < (int)(rate * 0.05)) return wav;
        var hop = Math.Max(1, (int)(rate * 0.01));
        var frames = wav.Length / hop;
        var threshold = Math.Pow(10, threshDb / 20.0);
        int first = -1, last = -1;
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var i = f * hop; i < (f + 1) * hop; i++) sum += (double)wav[i] * wav[i];
            if (Math.Sqrt(sum / hop + 1e-12) <= threshold) continue;
            if (first < 0) first = f;
            last = f;
        }
        if (first < 0) return wav;
        var keep = (int)(rate * keepSeconds);
        var start = Math.Max(0, first * hop - keep);
        var end = Math.Min(wav.Length, (last + 1) * hop + keep);
        return wav[start..end];
    }

    public static float[] Join(IReadOnlyList<float[]> wavs, int rate, double gapSeconds = ChunkGapSeconds)
    {
        var parts = wavs.Where(w => w is { Length: > 0 }).ToList();
        if (parts.Count == 0) return [];
        if (parts.Count == 1) return parts[0];
        var gap = new float[(int)(rate * gapSeconds)];
        var fade = Math.Max(1, (int)(rate * 0.008));
        var output = new List<float>();
        for (var i = 0; i < parts.Count; i++)
        {
            var wav = (float[])TrimSilence(parts[i], rate).Clone();
            if (wav.Length > 2 * fade)
                for (var k = 0; k < fade; k++)
                {
                    var g = fade == 1 ? 0f : (float)k / (fade - 1);
                    wav[k] *= g;
                    wav[^(k + 1)] *= g;
                }
            output.AddRange(wav);
            if (i != parts.Count - 1) output.AddRange(gap);
        }
        return output.ToArray();
    }
}
