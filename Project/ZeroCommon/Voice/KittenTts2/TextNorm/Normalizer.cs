// Port of kitten_text_processing (Apache-2.0 AND MIT; NeMo 1.2.0 token parser / ordering / punctuation helpers,
// sacremoses-derived detokenizer) and of kittenml/kittentts2/text.py's normalize_text wrapper (Apache-2.0).
// Python indexes strings by code point; where indices matter (parser, punctuation pass) this works on Runes.
using System.Text;
using System.Text.RegularExpressions;

namespace Agent.Common.Voice.KittenTts2.TextNorm;

/// <summary>NeMo-style written-to-spoken normalizer over the bundled byte transducers (normalizer.py).</summary>
internal sealed class Normalizer
{
    private readonly Fst _tagger, _verbalizer;
    private readonly Fst? _post;
    private readonly string _lang;
    private const int MaxPermutationsPerSplit = 729;

    public Normalizer(string folder, string lang = "en")
    {
        _lang = lang;
        _tagger = new Fst(Path.Combine(folder, "tagger.fst.gz"));
        _verbalizer = new Fst(Path.Combine(folder, "verbalizer.fst.gz"));
        var post = Path.Combine(folder, "post.fst.gz");
        _post = File.Exists(post) ? new Fst(post, projectOutput: true) : null;
    }

    /// <summary>normalizer.normalize_text: the backend's convenience API (punct_post_process on).</summary>
    public string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        return Normalize(text, punctPostProcess: true).Trim();
    }

    public string Normalize(string text, bool punctPostProcess)
    {
        var original = text;
        text = PyStrip(text);
        if (text.Length == 0) return text;
        var tagged = _tagger.Rewrite(text);
        var tokens = new TokenParser(tagged).Parse();
        var chunks = new List<string>();
        try
        {
            foreach (var group in Ordering.Split(tokens, MaxPermutationsPerSplit))
            {
                var done = false;
                foreach (var candidate in Ordering.GeneratePermutations(group))
                {
                    try
                    {
                        chunks.Add(_verbalizer.Rewrite(candidate));
                        done = true;
                        break;
                    }
                    catch (NoPathException) { }
                }
                if (!done) return Escape(text);
            }
        }
        catch (ArgumentException) { return Escape(text); }   // Python: ValueError from the split / permutation
        var output = Regex.Replace(string.Join(" ", chunks), " {2,}", " ");
        if (_post is not null && PyStrip(output).Length > 0) output = _post.Rewrite(PyStrip(output));
        if (punctPostProcess) output = Punctuation.PostProcess(original, Detokenizer.Detokenize(output, _lang));
        return output;
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]");

    internal static string PyStrip(string s) => s.Trim();
}

/// <summary>A parsed token value: a string, a bool (preserve_order: true) or an ordered map.</summary>
internal sealed class TokenMap
{
    public readonly List<(string Key, object Value)> Items = [];

    public void Set(string key, object value)
    {
        var i = Items.FindIndex(p => p.Key == key);
        if (i >= 0) Items[i] = (key, value);
        else Items.Add((key, value));
    }

    public bool Has(string key) => Items.Any(p => p.Key == key);
}

/// <summary>_token_parser.py</summary>
internal sealed class TokenParser
{
    private const string PreserveOrder = "preserve_order";
    private readonly int[] _text;
    private int _index;
    private int _char;              // -1 = EOS
    private const int Eos = -1;

    public TokenParser(string text)
    {
        _text = text.EnumerateRunes().Select(r => r.Value).ToArray();
        _char = _text[0];
        _index = 0;
    }

    public List<TokenMap> Parse()
    {
        var l = new List<TokenMap>();
        while (ParseWs())
        {
            var token = ParseToken();
            if (token is null) break;
            l.Add(token);
        }
        return l;
    }

    private TokenMap? ParseToken()
    {
        var d = new TokenMap();
        var key = ParseStringKey();
        if (key is null) return null;
        ParseWs();
        object value;
        if (key == PreserveOrder)
        {
            ParseChar(':');
            ParseWs();
            foreach (var c in "true") ParseChar(c);
            value = true;
        }
        else value = ParseTokenValue();
        d.Set(key, value);
        return d;
    }

    private object ParseTokenValue()
    {
        if (_char == ':')
        {
            ParseChar(':');
            ParseWs();
            ParseChar('"');
            var s = ParseStringValue();
            ParseChar('"');
            return s!;
        }
        if (_char == '{')
        {
            var d = new TokenMap();
            ParseChar('{');
            foreach (var tok in Parse())
                foreach (var (k, v) in tok.Items) d.Set(k, v);
            ParseChar('}');
            return d;
        }
        throw new ArgumentException("token value");
    }

    private void ParseChar(int exp)
    {
        if (_char != exp) throw new ArgumentException($"expected '{(char)exp}'");
        Read();
    }

    private static bool KeyChar(int c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';

    private string? ParseStringKey()
    {
        if (_char == Eos || _char is ' ' or '\t' or '\n' or '\r' or '\x0b' or '\x0c') throw new ArgumentException("key");
        var sb = new StringBuilder();
        while (_char != Eos && KeyChar(_char))
        {
            sb.Append((char)_char);
            if (!Read()) throw new ArgumentException("key at EOS");
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    private string? ParseStringValue()
    {
        if (_char == Eos) throw new ArgumentException("value at EOS");
        var sb = new StringBuilder();
        while (_char != '"' || Next() != ' ')
        {
            sb.Append(char.ConvertFromUtf32(_char));
            if (!Read()) throw new ArgumentException("value at EOS");
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    // Python: self.text[self.index + 1] - an IndexError past the end.
    private int Next() => _index + 1 < _text.Length ? _text[_index + 1] : throw new ArgumentException("index");

    private bool ParseWs()
    {
        var notEos = _char != Eos;
        while (notEos && _char == ' ') notEos = Read();
        return notEos;
    }

    private bool Read()
    {
        if (_index < _text.Length - 1)
        {
            _index++;
            _char = _text[_index];
            return true;
        }
        _char = Eos;
        return false;
    }
}

/// <summary>_ordering.py</summary>
internal static class Ordering
{
    private static long Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);

    private static long Estimate(TokenMap group)
    {
        long n = 1;
        foreach (var (_, inner) in group.Items)
            if (inner is TokenMap m) n *= Estimate(m);
        return n * Factorial(group.Items.Count);
    }

    public static List<List<TokenMap>> Split(List<TokenMap> tokens, int max)
    {
        var splits = new List<List<TokenMap>>();
        var prevEnd = 0;
        long current = 1;
        for (var i = 0; i < tokens.Count; i++)
        {
            var n = Estimate(tokens[i]);
            if (n * current > max)
            {
                splits.Add(tokens[prevEnd..i]);
                prevEnd = i;
                current = 1;
            }
            if (n > max) throw new ArgumentException("unsplittable token group");
            current *= n;
        }
        splits.Add(tokens[prevEnd..]);
        return splits;
    }

    private static IEnumerable<int[]> Permutations(int n)
    {
        // itertools.permutations order (lexicographic by position)
        var idx = Enumerable.Range(0, n).ToArray();
        yield return (int[])idx.Clone();
        while (true)
        {
            var i = n - 2;
            while (i >= 0 && idx[i] > idx[i + 1]) i--;
            if (i < 0) yield break;
            var j = n - 1;
            while (idx[j] < idx[i]) j--;
            (idx[i], idx[j]) = (idx[j], idx[i]);
            Array.Reverse(idx, i + 1, n - i - 1);
            yield return (int[])idx.Clone();
        }
    }

    public static List<string> Permute(TokenMap d)
    {
        var l = new List<string>();
        var perms = d.Has("preserve_order")
            ? [Enumerable.Range(0, d.Items.Count).ToArray()]
            : Permutations(d.Items.Count);
        foreach (var perm in perms)
        {
            var subl = new List<string> { "" };
            foreach (var pi in perm)
            {
                var (k, v) = d.Items[pi];
                switch (v)
                {
                    case string s:
                        subl = subl.Select(x => x + $"{k}: \"{s}\" ").ToList();
                        break;
                    case TokenMap m:
                        var rec = Permute(m);
                        subl = subl.SelectMany(x => rec.Select(r => x + $" {k} {{ " + r + " } ")).ToList();
                        break;
                    case bool:
                        subl = subl.Select(x => x + $"{k}: true ").ToList();
                        break;
                    default:
                        throw new ArgumentException($"Key: {k}");
                }
            }
            l.AddRange(subl);
        }
        return l;
    }

    public static IEnumerable<string> GeneratePermutations(List<TokenMap> tokens)
    {
        var lists = tokens.Select(Permute).ToList();
        return Product(lists, 0, "");
    }

    private static IEnumerable<string> Product(List<List<string>> lists, int at, string prefix)
    {
        if (at == lists.Count) { yield return prefix; yield break; }
        foreach (var s in lists[at])
            foreach (var r in Product(lists, at + 1, prefix + s))
                yield return r;
    }
}

/// <summary>_detokenize.py (Moses-compatible, character data from sacremoses).</summary>
internal static class Detokenizer
{
    private static readonly int[] AlphaStarts = UnicodeData.AlphaRanges.Select(r => r.Start).ToArray();

    private static bool Alpha(int c)
    {
        var i = Array.BinarySearch(AlphaStarts, c);
        if (i < 0) i = ~i - 1;
        return i >= 0 && c <= UnicodeData.AlphaRanges[i].End;
    }

    private static readonly (int, int)[] Cjk =
    [
        (4352, 4607), (11904, 42191), (43072, 43135), (44032, 55215), (63744, 64255), (65072, 65103),
        (65381, 65500), (94208, 101119), (110592, 110895), (110960, 111359), (131072, 196607),
    ];

    private static bool IsCjk(int c) => Cjk.Any(r => r.Item1 <= c && c <= r.Item2);

    private static int First(string s) => s.EnumerateRunes().First().Value;
    private static int Last(string s) => s.EnumerateRunes().Last().Value;
    private static int[] Runes(string s) => s.EnumerateRunes().Select(r => r.Value).ToArray();

    public static string Detokenize(string text, string lang)
    {
        var tokens = Regex.Replace(" " + text + " ", " @-@ ", "-").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var quotes = new Dictionary<string, int>();
        var space = " ";
        var result = new StringBuilder();
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var r = Runes(token);
            if (IsCjk(r[0]) && lang != "ko")
            {
                result.Append((i > 0 && IsCjk(Last(tokens[i - 1])) ? "" : space) + token);
                space = " ";
            }
            else if (r.All(c => UnicodeData.CurrencySymbols.Contains(c) || "([{¿¡".Contains((char)c) && c < 0x10000))
            {
                result.Append(space + token);
                space = "";
            }
            else if (Regex.IsMatch(token, @"^[,\.?!:;\\%}\])]+$"))
            {
                result.Append((lang == "fr" && Regex.IsMatch(token, @"^[?!:;\\%]$") ? " " : "") + token);
                space = " ";
            }
            else if (lang == "en" && i > 0 && r.Length > 1 && r[0] == '\'' && Alpha(r[1]))
            {
                result.Append(token);
                space = " ";
            }
            else if ((lang is "fr" or "it") && i + 1 < tokens.Length && r.Length > 1 && r[^1] == '\'' && Alpha(r[^2])
                     && Alpha(First(tokens[i + 1])))
            {
                result.Append(space + token);
                space = "";
            }
            else if (Regex.IsMatch(token, "^['\"„“`]+$"))
            {
                var key = token.All(c => "„“”".Contains(c)) ? "\"" : token;
                var count = quotes.GetValueOrDefault(key, 0);
                if (count % 2 == 1 || (lang == "en" && token == "'" && i > 0 && tokens[i - 1].EndsWith('s')))
                {
                    result.Append(token);
                    space = " ";
                    if (count % 2 == 1) quotes[key] = count + 1;
                }
                else
                {
                    result.Append(space + token);
                    space = "";
                    quotes[key] = count + 1;
                }
            }
            else
            {
                result.Append(space + token);
                space = " ";
            }
        }
        return Regex.Replace(result.ToString(), " {2,}", " ").Trim();
    }
}

/// <summary>_punctuation.py post_process_punct (default string.punctuation only).</summary>
internal static class Punctuation
{
    private const string PyPunctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    public static string PostProcess(string input, string normalized)
    {
        if (input.Contains("``") && !normalized.Contains("``")) input = input.Replace("``", "\"");
        var inp = input.EnumerateRunes().Select(r => r.ToString()).ToList();
        var outp = normalized.EnumerateRunes().Select(r => r.ToString()).ToList();
        var marks = PyPunctuation.Where(c => inp.Contains(c.ToString())).Select(c => c.ToString()).ToList();
        foreach (var punct in marks)
        {
            try
            {
                var equal = inp.Count(x => x == punct) == outp.Count(x => x == punct);
                int idxIn = 0, idxOut = 0;
                while (inp.Skip(idxIn).Contains(punct))
                {
                    idxOut = IndexOf(outp, punct, idxOut);
                    idxIn = IndexOf(inp, punct, idxIn);
                    bool Valid() =>
                        (idxOut > 0 && idxIn > 0 && outp[idxOut - 1] == inp[idxIn - 1]) ||
                        (idxOut < outp.Count - 1 && idxIn < inp.Count - 1 && outp[idxOut + 1] == inp[idxIn + 1]);
                    if (!equal && !Valid())
                    {
                        idxIn++;
                        continue;
                    }
                    if (idxIn > 0 && idxOut > 0)
                    {
                        if (outp[idxOut - 1] == " " && inp[idxIn - 1] != " ") outp[idxOut - 1] = "";
                        else if (outp[idxOut - 1] != " " && inp[idxIn - 1] == " ") outp[idxOut - 1] += " ";
                    }
                    if (idxIn < inp.Count - 1 && idxOut < outp.Count - 1)
                    {
                        if (outp[idxOut + 1] == " " && inp[idxIn + 1] != " ") outp[idxOut + 1] = "";
                        else if (outp[idxOut + 1] != " " && inp[idxIn + 1] == " ") outp[idxOut] = outp[idxOut] + " ";
                    }
                    idxOut++;
                    idxIn++;
                }
            }
            catch (InvalidOperationException) { }   // Python: bare except around list.index failing
        }
        return Regex.Replace(string.Concat(outp), " +", " ");
    }

    private static int IndexOf(List<string> list, string value, int start)
    {
        for (var i = start; i < list.Count; i++) if (list[i] == value) return i;
        throw new InvalidOperationException("not in list");
    }
}

/// <summary>kittenml/kittentts2/text.py normalize_text: what wraps the backend (tags, math glyphs, brackets, dashes).</summary>
internal sealed class KittenText(Normalizer backend)
{
    private static readonly string[] ExpressionTags =
        ["mundane", "nervous", "tender", "angry", "excited", "stern", "sad", "contemplative", "surprised", "joyful"];
    private static readonly string[] VocalEventTags = ["pause", "sigh", "gasp", "laugh", "giggle", "sob", "scoff", "growl", "um", "gulp"];

    private static readonly Regex TagSpan = new(
        @"\[(?:" + string.Join("|", ExpressionTags.Select(Regex.Escape)) + @")\]|<(?:" +
        string.Join("|", VocalEventTags.Select(Regex.Escape)) + ")>", RegexOptions.IgnoreCase);
    private static readonly Regex EmphasisSpan = new(@"\(\(\(([^()\n]{1,80})\)\)\)");
    private static string Placeholder(int i) => $"\ue000{i}\ue001";

    /// <summary>text.has_expression_tag: an [emotion], &lt;event&gt; or (((emphasis))) marker turns on the {emo: 1} control.</summary>
    public static bool HasExpressionTag(string text) => TagSpan.IsMatch(text ?? "") || EmphasisSpan.IsMatch(text ?? "");

    private static readonly Regex UiGlyphs = new("[\u2022\u2023\u2043\u25e6\u25b8\u21b3\u2192\u2190\u2191\u2193]+");

    private static readonly Dictionary<string, string> MathGlyphWords = BuildMath();
    private static readonly Regex MathGlyphRe = new(string.Join("|",
        MathGlyphWords.Keys.OrderByDescending(k => k.EnumerateRunes().Count()).Select(Regex.Escape)));

    private static Dictionary<string, string> BuildMath()
    {
        var d = new Dictionary<string, string>
        {
            ["\u03b1"] = "alpha", ["\u03b2"] = "beta", ["\u03b3"] = "gamma", ["\u03b4"] = "delta", ["\u03b5"] = "epsilon", ["\u03b6"] = "zeta",
            ["\u03b7"] = "eta", ["\u03b8"] = "theta", ["\u03b9"] = "iota", ["\u03ba"] = "kappa", ["\u03bb"] = "lambda", ["\u03bc"] = "mu",
            ["\u03bd"] = "nu", ["\u03be"] = "xi", ["\u03c0"] = "pi", ["\u03c1"] = "rho", ["\u03c3"] = "sigma", ["\u03c4"] = "tau",
            ["\u03c5"] = "upsilon", ["\u03c6"] = "phi", ["\u03d5"] = "phi", ["\u03c7"] = "chi", ["\u03c8"] = "psi", ["\u03c9"] = "omega",
            ["\u0393"] = "gamma", ["\u0394"] = "delta", ["\u0398"] = "theta", ["\u039b"] = "lambda", ["\u039e"] = "xi", ["\u03a0"] = "pi",
            ["\u03a3"] = "sigma", ["\u03a6"] = "phi", ["\u03a8"] = "psi", ["\u03a9"] = "omega",
            ["="] = "equals", ["\u2260"] = "is not equal to", ["\u2248"] = "approximately equals",
            ["\u2264"] = "is less than or equal to", ["\u2265"] = "is greater than or equal to",
            ["\u00b1"] = "plus or minus", ["\u00d7"] = "times", ["\u00f7"] = "divided by", ["\u00b7"] = "dot",
            ["\u2212"] = "minus", ["\u2217"] = "star", ["\u2218"] = "composed with", ["\u22a4"] = "transpose",
            ["\u223c"] = "distributed as", ["\u221d"] = "proportional to", ["\u2261"] = "is equivalent to",
            ["\u2208"] = "in", ["\u2209"] = "not in", ["\u2282"] = "subset of", ["\u2286"] = "subset of", ["\u222a"] = "union",
            ["\u2229"] = "intersect", ["\u2295"] = "plus", ["\u2297"] = "times", ["^"] = "to the",
            ["\u2211"] = "sum of", ["\u220f"] = "product of", ["\u222b"] = "integral of", ["\u2202"] = "partial",
            ["\u2207"] = "gradient of", ["\u221a"] = "square root of", ["\u221e"] = "infinity", ["\u2206"] = "delta",
            ["\u2200"] = "for all", ["\u2203"] = "there exists", ["\u21d2"] = "implies", ["\u21d4"] = "if and only if",
            ["\u2225"] = "norm", ["\u2016"] = "norm", ["\u230a"] = "floor of", ["\u230b"] = "", ["\u2308"] = "ceiling of", ["\u2309"] = "",
            ["\u211d"] = "R", ["\u2115"] = "N", ["\u2124"] = "Z", ["\u211a"] = "Q", ["\u2102"] = "C", ["\U0001d53c"] = "the expectation of",
        };
        const string sup = "\u2070\u00b9\u00b2\u00b3\u2074\u2075\u2076\u2077\u2078\u2079", sub = "\u2080\u2081\u2082\u2083\u2084\u2085\u2086\u2087\u2088\u2089";
        for (var i = 0; i < 10; i++)
        {
            d[sup[i].ToString()] = i.ToString();
            d[sub[i].ToString()] = i.ToString();
        }
        return d;
    }

    private static readonly Regex Ellipsis = new(@"(?:\.\s+){2,}\.|\u2026|\.\.\.");
    private static readonly Regex StraightQuotePair = new("\"([^\"\\n]*)\"");
    private static readonly Regex NonTagBracketSpan = new(@"\[([^\[\]\n]{1,400})\]|<([^<>\n]{1,60})>|\(([^()\n]{1,400})\)");
    private static readonly Regex CommaCollision = new(@",\s*([,.!?;:])");
    private static readonly Regex TrailingNonterminal = new(@"[,;:\-\u2013\u2014]+\s*$");
    private static readonly HashSet<char> AlreadyTerminated = [.. ".!?\"\u2019\u201d')]"];
    private static readonly Regex Dash = new(@"\s*(?:[\u2014\u2013]|--)\s*");
    private static readonly (Regex, string)[] DashFix =
    [
        (new Regex(@",\s*,+"), ", "),
        (new Regex(@"\s+([,.!?;:])"), "$1"),
        (new Regex(@",\s*([.!?])"), "$1"),
        (new Regex(@"\s{2,}"), " "),
        (new Regex(@"^\s*,\s*"), ""),
    ];

    public string Normalize(string text)
    {
        var tags = new List<string>();
        string Swap(Match m) { tags.Add(m.Value); return Placeholder(tags.Count - 1); }
        var p = EmphasisSpan.Replace(TagSpan.Replace(text ?? "", Swap), Swap);
        p = Regex.Replace(p, @"\s*\n+\s*", " ");
        p = Regex.Replace(p, @"(?<=[a-z])([.!?;:])(?=[A-Z])", "$1 ");
        p = SpeakMathGlyphs(p);
        p = Regex.Replace(p, @"\b([vV])(\d+(?:\.\d+)+)", m => m.Groups[1].Value + " " + string.Join(" point ", m.Groups[2].Value.Split('.')));
        p = Regex.Replace(p, @"(?<=\d)\s*([?!])(?=\s|$)", " $1");

        var raw = DashesToCommas(UiGlyphs.Replace(ReformatNonTagBrackets(p), " "));
        if (raw.Trim().Length == 0) return Restore(raw, tags);
        string spoken;
        try { spoken = (backend.NormalizeText(SmartQuotePairs(raw)) ?? "").Trim(); }
        catch (Exception ex) when (ex is NoPathException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"[normalize] {ex.GetType().Name}, speaking raw text: {ex.Message}");
            spoken = "";
        }
        if (spoken.Length == 0) return Restore(CapitalizeAndTerminate(raw), tags);
        return Restore(CapitalizeAndTerminate(DashesToCommas(spoken)), tags);
    }

    private static string Restore(string text, List<string> tags)
    {
        for (var i = 0; i < tags.Count; i++) text = text.Replace(Placeholder(i), tags[i]);
        return text;
    }

    private static string SpeakMathGlyphs(string text)
    {
        text = Ellipsis.Replace(text, ", ");
        text = MathGlyphRe.Replace(text, m => $" {MathGlyphWords[m.Value]} ");
        return Regex.Replace(text, "[ \t]{2,}", " ");
    }

    private static string ReformatNonTagBrackets(string text)
    {
        text = NonTagBracketSpan.Replace(text, m =>
        {
            var inner = (m.Groups[1].Success ? m.Groups[1] : m.Groups[2].Success ? m.Groups[2] : m.Groups[3]).Value.Trim();
            return $", {inner},";
        });
        while (true)
        {
            var collapsed = CommaCollision.Replace(text, "$1");
            if (collapsed == text) return collapsed;
            text = collapsed;
        }
    }

    private static string SmartQuotePairs(string text) => StraightQuotePair.Replace(text, m => "\u201c" + m.Groups[1].Value + "\u201d");

    private static string DashesToCommas(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = Dash.Replace(text, ", ");
        foreach (var (re, rep) in DashFix) text = re.Replace(text, rep);
        return text.Trim();
    }

    private static string CapitalizeAndTerminate(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = Regex.Replace(text, @"\s+([?!])", "$1");
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsLetter(text[i])) continue;
            text = text[..i] + char.ToUpperInvariant(text[i]) + text[(i + 1)..];
            break;
        }
        var stripped = text.TrimEnd();
        if (stripped.Length == 0) return text;
        if (AlreadyTerminated.Contains(stripped[^1])) return stripped;
        stripped = TrailingNonterminal.Replace(stripped, "").TrimEnd();
        return stripped.Length > 0 ? stripped + "." : stripped;
    }
}
