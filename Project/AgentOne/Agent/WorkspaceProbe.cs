using System.Diagnostics;
using System.Text;

namespace AgentOne.Agent;

/// <summary>One file that mentions the request's words, and the line that shows it best.</summary>
public sealed record ProbeHit(string Path, int Line, string Text, double Score, IReadOnlyList<string> Terms);

/// <summary>What a search of the workspace for the request's own words found.</summary>
public sealed record ProbeResult(IReadOnlyList<string> Terms, IReadOnlyList<ProbeHit> Hits, int FilesScanned)
{
    public static ProbeResult Empty { get; } = new([], [], 0);

    public bool Found => Hits.Count > 0;

    /// <summary>The hits as material for a model, or empty.</summary>
    public string Material()
    {
        if (!Found) return "";
        var sb = new StringBuilder("[workspace] Files in this workspace that mention the request's words (")
            .Append(string.Join(", ", Terms.Take(6)))
            .AppendLine(") — what the request refers to may already be here. Read them before assuming anything is missing:");
        foreach (var hit in Hits)
            sb.Append("- ").Append(hit.Path).Append(':').Append(hit.Line).Append(": ").AppendLine(hit.Text);
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Searches the workspace's text files for the request's own words before a
/// design is made or a turn hands work back. Measured (2026-11-06,
/// music-harness): "종말의 그날 1,2 를 통합해 뮤비작성" — the workspace held a todo
/// with both episodes' video ids, the genre, the art style and the rules for
/// that very song; one grep for "종말" finds it. Nothing looked. The design
/// assumed local video files "(recommended)", and the turn ended asking the
/// person to put them in a folder it had just created.
///
/// Language-neutral by construction: words are cut on letters, a Korean word
/// loses its particle (종말의 → 종말), and the weight of a word is how few files
/// contain it, so a generic word ("진행", "make") scores near nothing without a
/// stop list having to know it. Bounded in files and time; never fatal.
/// </summary>
public static class WorkspaceProbe
{
    public const int MaxHits = 8;

    /// <summary>A word in at most this share of the scanned files (or three) is rare enough to point at a file.</summary>
    internal const double RareShare = 0.005;
    private const int MaxTerms = 10;
    private const int MaxFiles = 6_000;
    private const long MaxFileBytes = 512 * 1024;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".rst", ".adoc", ".yaml", ".yml", ".toml", ".json", ".csv", ".org"
    };

    // Particles and verb endings that hang off a Korean word, longest first.
    private static readonly string[] Endings =
    [
        "에서는", "으로는", "에게서", "해보자", "해주세요", "합니다", "했는데", "하는데",
        "까지", "부터", "에서", "으로", "에게", "하고", "이랑", "처럼", "보다", "해줘", "해죠", "하자", "해서", "하는", "했던", "들을", "들의", "들이",
        "의", "을", "를", "이", "가", "은", "는", "에", "로", "와", "과", "도", "만", "랑", "해", "들"
    ];

    // Words that ask for work rather than name a thing. The weighting already
    // shrinks them; dropping them keeps the term list readable.
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "진행", "작성", "통합", "만들", "만들어", "해보", "보자", "확인", "분석", "이용", "사용", "작업", "그리고", "이제", "지금",
        "다시", "같이", "정리", "해주", "부탁", "어떻게", "무엇", "내용", "관련", "대해", "위해", "있는", "없는", "해볼까",
        "the", "and", "for", "with", "this", "that", "make", "create", "please", "use", "using", "from", "into",
        "about", "let", "lets", "can", "you", "what", "how", "now", "then", "build", "write", "run", "check", "work"
    };

    /// <summary>The words of a request worth searching for, then the two-word phrases among them.</summary>
    internal static (IReadOnlyList<string> Terms, IReadOnlyList<string> Phrases) TermsOf(string request)
    {
        var raw = new List<string>();
        var current = new StringBuilder();
        foreach (var c in request + " ")
        {
            if (char.IsLetterOrDigit(c)) { current.Append(c); continue; }
            if (current.Length > 0) { raw.Add(current.ToString()); current.Clear(); }
        }

        var terms = new List<string>();
        var phrases = new List<string>();
        string? previousRaw = null;
        foreach (var word in raw)
        {
            var stem = Stem(word);
            var keep = stem.Length >= (IsHangul(stem) ? 2 : 3)
                       && !stem.All(char.IsDigit)
                       && !Generic.Contains(stem);
            if (keep)
            {
                if (!terms.Contains(stem, StringComparer.OrdinalIgnoreCase)) terms.Add(stem);
                // "종말의 그날": the first word as written, the second without its particle.
                if (previousRaw is not null) phrases.Add(previousRaw + " " + stem);
                previousRaw = word;
            }
            else previousRaw = null;
        }

        return (terms.Take(MaxTerms).ToList(), phrases.Take(MaxTerms).ToList());
    }

    internal static string Stem(string word)
    {
        if (!IsHangul(word)) return word.ToLowerInvariant();
        foreach (var ending in Endings)
        {
            if (word.Length - ending.Length >= 2 && word.EndsWith(ending, StringComparison.Ordinal))
                return word[..^ending.Length];
        }
        return word;
    }

    private static bool IsHangul(string word) => word.Any(c => c is >= '가' and <= '힣');

    public static Task<ProbeResult> SearchAsync(string root, string request, CancellationToken ct) =>
        Task.Run(() => Search(root, request, ct), ct);

    public static ProbeResult Search(string root, string request, CancellationToken ct = default)
    {
        var (terms, phrases) = TermsOf(request);
        if (terms.Count == 0 || !Directory.Exists(root)) return ProbeResult.Empty;

        var clock = Stopwatch.StartNew();
        var files = new List<(string Rel, bool[] Has, bool[] PhraseHas, string[] Lines, bool[] InName)>();
        var scanned = 0;

        foreach (var path in Walk(root))
        {
            if (ct.IsCancellationRequested || scanned >= MaxFiles || clock.Elapsed > Budget) break;
            scanned++;

            string text;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaxFileBytes) continue;
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (text.Contains('\0')) continue;

            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            var has = terms.Select(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)).ToArray();
            var inName = terms.Select(t => rel.Contains(t, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (!has.Any(h => h) && !inName.Any(h => h)) continue;

            var phraseHas = phrases.Select(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)).ToArray();
            files.Add((rel, has, phraseHas, text.Replace("\r\n", "\n").Split('\n'), inName));
        }

        if (files.Count == 0) return new ProbeResult(terms, [], scanned);

        // A word in every file says nothing about which file is meant.
        var n = Math.Max(scanned, 1);
        var idf = terms.Select((_, i) =>
        {
            var df = files.Count(f => f.Has[i] || f.InName[i]);
            return Math.Log(1 + (double)n / Math.Max(df, 1));
        }).ToArray();
        var phraseIdf = phrases.Select((_, i) =>
        {
            var df = files.Count(f => f.PhraseHas[i]);
            return df == 0 ? 0 : Math.Log(1 + (double)n / df);
        }).ToArray();

        var scored = files.Select(f =>
        {
            var score = 0.0;
            for (var i = 0; i < terms.Count; i++)
            {
                if (f.Has[i]) score += idf[i];
                if (f.InName[i]) score += idf[i];
            }
            for (var i = 0; i < phrases.Count; i++)
                if (f.PhraseHas[i]) score += 2 * phraseIdf[i];
            return (File: f, Score: score);
        }).OrderByDescending(x => x.Score).ToList();

        // A file counts only when it holds a word few files hold, or the phrase
        // as written. Measured (2026-11-07): "생성된 곡들 검토해서 추천해줘" matched
        // eight files on common words alone, and the hand-back check then sent
        // the turn to read them — a todo, a lore page, nothing about takes.
        var rareDf = Math.Max(3, (int)(n * RareShare));
        var rare = terms.Select((_, i) => files.Count(f => f.Has[i] || f.InName[i]) <= rareDf).ToArray();
        scored = scored.Where(x => x.File.PhraseHas.Any(h => h)
                                   || terms.Where((_, i) => rare[i] && (x.File.Has[i] || x.File.InName[i])).Any()).ToList();
        if (scored.Count == 0) return new ProbeResult(terms, [], scanned);

        var best = scored[0].Score;
        var hits = new List<ProbeHit>();
        foreach (var (f, score) in scored)
        {
            if (hits.Count >= MaxHits || score < best * 0.5) break;
            var (line, lineText) = BestLine(f.Lines, terms, phrases);
            var matched = terms.Where((_, i) => f.Has[i] || f.InName[i]).ToList();
            hits.Add(new ProbeHit(f.Rel, line, Clip(lineText.Trim(), 200), Math.Round(score, 2), matched));
        }

        return new ProbeResult(terms, hits, scanned);
    }

    /// <summary>The line that carries the most of the request's words (a phrase counts double).</summary>
    private static (int Line, string Text) BestLine(string[] lines, IReadOnlyList<string> terms, IReadOnlyList<string> phrases)
    {
        var bestIndex = 0;
        var bestScore = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var score = terms.Count(t => line.Contains(t, StringComparison.OrdinalIgnoreCase))
                        + 2 * phrases.Count(p => line.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (score > bestScore) { bestScore = score; bestIndex = i; }
        }
        return (bestIndex + 1, lines.Length == 0 ? "" : lines[bestIndex]);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>Text files under the root, skipping what an agent never needs to see (same rule as the file tools, plus build caches).</summary>
    private static IEnumerable<string> Walk(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] subdirs, entries;
            try
            {
                entries = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var file in entries)
                if (TextExtensions.Contains(Path.GetExtension(file))) yield return file;

            foreach (var sub in subdirs.OrderByDescending(s => s, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(sub);
                if (IsExcluded(name)) continue;
                try { if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                pending.Push(sub);
            }
        }
    }

    internal static bool IsExcluded(string name) =>
        (name.StartsWith('.') && !name.Equals(".claude", StringComparison.OrdinalIgnoreCase)) ||
        name is "bin" or "obj" or "node_modules" or "dist" or "build" or "__pycache__" or "venv"
            or "Library" or "Temp" or "Logs" or "Packages" or "packages" or "target" or "out";
}
