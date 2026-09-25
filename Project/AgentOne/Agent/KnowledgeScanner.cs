using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentOne.Graph;
using AgentOne.Llm.Decision;

namespace AgentOne.Agent;

/// <summary>One section of a Markdown file: the headings above it, and what it says.</summary>
/// <param name="Heading">The heading path, "Build › Release"; the file name for text before the first heading.</param>
/// <param name="Ordinal">Which occurrence of this heading path in the file — keeps ids apart when a heading repeats.</param>
public sealed record DocSection(string Heading, string Body, int Ordinal);

/// <summary>What one `knowledge init` / `update` did, in numbers. Guidelines / Knowledge count the sections judged in this run.</summary>
public sealed record ScanReport(
    int Files, int Unchanged, int NewDocs, int ChangedDocs, int RemovedDocs, int Skipped,
    int Added, int Updated, int Removed, int Kept,
    int Guidelines, int Knowledge, int EngineCalls, int ByRule, string Engine, TimeSpan Elapsed)
{
    public IReadOnlyList<string> Describe() =>
    [
        $"files      {Files} markdown · {Unchanged} unchanged · {NewDocs} new · {ChangedDocs} changed · {RemovedDocs} gone" + (Skipped > 0 ? $" · {Skipped} skipped (too big)" : ""),
        $"sections   +{Added} added · ~{Updated} updated · -{Removed} removed · {Kept} kept as they were",
        EngineCalls + ByRule == 0
            ? "judged     nothing — no section changed"
            : $"judged     {EngineCalls + ByRule} section{(EngineCalls + ByRule == 1 ? "" : "s")} → {Guidelines} guideline · {Knowledge} knowledge  ({Engine}: {EngineCalls} calls" + (ByRule > 0 ? $", {ByRule} by rule" : "") + ")",
        $"took       {Elapsed.TotalSeconds:0.0}s"
    ];
}

/// <summary>
/// Builds the workspace's document knowledge into the graph: every Markdown
/// file under the root is split into sections, each section is put to the
/// decision engine — guideline or knowledge? — and stored with the edge that
/// answer names. It is incremental by content: a file whose hash has not
/// changed is not read past the hash, and inside a changed file only the
/// sections whose text changed are judged again; sections and files that are
/// gone are removed. `rebuild` judges everything again.
///
/// Without an engine (no TypeSafe key) the classification falls back to a
/// word rule — imperative markers in English and Korean — and the rationale
/// says so, so a later run with a key can be told apart.
/// </summary>
public sealed partial class KnowledgeScanner(KnowledgeGraph graph, string root, SmartRouter? router)
{
    /// <summary>Larger files are reference dumps, not documents; they are counted and skipped.</summary>
    public const int MaxFileBytes = 256 * 1024;

    /// <summary>A section with less than this much text is a heading over subsections, not something to judge.</summary>
    public const int MinBodyChars = 30;

    /// <summary>Engine calls in flight at once. Each is ~0.3 s; a repository has hundreds of sections.</summary>
    public int Parallelism { get; init; } = 4;

    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "out", "dist", "build", "packages", "vendor", "target", "__pycache__", "venv", "_cache"
    };

    /// <param name="scope">Only files under this folder (relative to the root); null for all. Files outside it are neither read nor removed.</param>
    public async Task<ScanReport> ScanAsync(bool rebuild, string? scope, Action<string>? progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var rootFull = Path.GetFullPath(root);
        var scopePrefix = scope is null ? null : Relative(rootFull, Path.GetFullPath(Path.Combine(rootFull, scope))).TrimEnd('/') + "/";
        if (scopePrefix == "./" || scopePrefix == "/") scopePrefix = null;

        int unchanged = 0, newDocs = 0, changedDocs = 0, removedDocs = 0, skipped = 0;
        int added = 0, updated = 0, removed = 0, kept = 0, guidelines = 0, knowledge = 0, engineCalls = 0, byRule = 0;

        var files = MarkdownFiles(rootFull).Where(f => scopePrefix is null || f.Rel.StartsWith(scopePrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        progress?.Invoke($"found {files.Count} markdown file{(files.Count == 1 ? "" : "s")} under {rootFull}{(scopePrefix is null ? "" : " / " + scopePrefix)}");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var (full, rel) in files)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            seen.Add(rel);

            if (new FileInfo(full).Length > MaxFileBytes) { skipped++; continue; }

            var content = Normalize(await File.ReadAllTextAsync(full, ct));
            var hash = Hash(content);
            var known = graph.DocHash(rel);
            if (!rebuild && known == hash) { unchanged++; continue; }
            if (known is null) newDocs++; else changedDocs++;

            var sections = Parse(content, Path.GetFileName(rel)).Where(s => s.Body.Trim().Length >= MinBodyChars).ToList();
            var stored = graph.DocSections(rel);
            var wanted = sections.Select(s => (Section: s, Id: SectionId(rel, s), Hash: Hash(s.Heading + "\n" + s.Body))).ToList();

            // Only what is new or changed goes to the engine.
            var toJudge = wanted.Where(w => rebuild || !stored.TryGetValue(w.Id, out var old) || old.Hash != w.Hash).ToList();
            kept += wanted.Count - toJudge.Count;

            if (toJudge.Count > 0)
                progress?.Invoke($"[{index}/{files.Count}] {rel} — judging {toJudge.Count} of {wanted.Count} section{(wanted.Count == 1 ? "" : "s")}");

            using var gate = new SemaphoreSlim(Math.Max(1, Parallelism));
            var judged = await Task.WhenAll(toJudge.Select(async w =>
            {
                await gate.WaitAsync(ct);
                try { return (w, Verdict: await ClassifyAsync(rel, w.Section, ct)); }
                finally { gate.Release(); }
            }));

            foreach (var (w, verdict) in judged)
            {
                if (verdict.ByEngine) engineCalls++; else byRule++;
                if (verdict.Kind == KnowledgeGraph.GuidelineKind) guidelines++; else knowledge++;
                if (stored.ContainsKey(w.Id)) updated++; else added++;

                var keywords = string.Join(' ', KnowledgeGraph.Keywords(w.Section.Heading + " " + w.Section.Body, 12));
                graph.PutDocSection(rel, w.Id, w.Section.Heading, w.Section.Body.Trim(), w.Hash, verdict.Kind, verdict.Why, keywords);
            }

            var live = wanted.Select(w => w.Id).ToHashSet();
            foreach (var gone in stored.Keys.Where(id => !live.Contains(id)))
            {
                graph.RemoveDocSection(gone);
                removed++;
            }

            graph.MarkDocScanned(rel, hash, wanted.Count);
        }

        // Files the graph knows that are no longer on disk (inside the scope).
        foreach (var doc in graph.DocPaths())
        {
            if (seen.Contains(doc)) continue;
            if (scopePrefix is not null && !doc.StartsWith(scopePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            removed += graph.DocSections(doc).Count;
            graph.RemoveDoc(doc);
            removedDocs++;
            progress?.Invoke($"removed {doc} (no longer on disk)");
        }

        return new ScanReport(files.Count, unchanged, newDocs, changedDocs, removedDocs, skipped,
            added, updated, removed, kept, guidelines, knowledge, engineCalls, byRule,
            router is null ? "rule (no TypeSafe key)" : "jev", clock.Elapsed);
    }

    private sealed record Verdict(string Kind, Rationale Why, bool ByEngine);

    private async Task<Verdict> ClassifyAsync(string docPath, DocSection section, CancellationToken ct)
    {
        var basis = $"{docPath} › {section.Heading}";
        if (router is not null)
        {
            var decision = await router.GuidelineOrKnowledgeAsync(docPath, section.Heading, section.Body, ct);
            if (decision.Ok && decision.Choice is SmartRouter.GuidelineOption or SmartRouter.KnowledgeOption)
            {
                var kind = decision.Choice == SmartRouter.GuidelineOption ? KnowledgeGraph.GuidelineKind : KnowledgeGraph.KnowledgeKind;
                return new Verdict(kind, new Rationale(SmartRouter.GuidelineQuestion, decision.Choice, decision.Confidence, basis), true);
            }
        }

        var (ruleKind, score) = ByRule(section.Heading, section.Body);
        return new Verdict(ruleKind, new Rationale("rule: imperative markers (no engine answer)", ruleKind, score, basis), false);
    }

    /// <summary>
    /// The fallback: a section reads as a guideline when it is dense with
    /// imperatives — must / never / always / do not, 반드시 / 금지 / 하지 말 / 할 것.
    /// The confidence is that density, capped, so the rationale shows how thin it was.
    /// </summary>
    public static (string Kind, double Confidence) ByRule(string heading, string body)
    {
        var text = (heading + "\n" + body).ToLowerInvariant();
        var hits = Imperative().Matches(text).Count;
        var lines = Math.Max(1, body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        var density = hits / (double)lines;
        var headingSaysRule = RuleHeading().IsMatch(heading.ToLowerInvariant());
        var guideline = headingSaysRule || density >= 0.25 || hits >= 4;
        var confidence = Math.Min(0.9, 0.5 + density / 2 + (headingSaysRule ? 0.2 : 0));
        return (guideline ? KnowledgeGraph.GuidelineKind : KnowledgeGraph.KnowledgeKind, Math.Round(guideline ? confidence : 1 - Math.Min(0.5, density), 2));
    }

    // ------------------------------------------------------------ parsing

    /// <summary>
    /// ATX headings split the file; a heading inside a code fence is code, not
    /// structure. Text before the first heading is a section named after the file.
    /// </summary>
    public static IReadOnlyList<DocSection> Parse(string content, string fileName)
    {
        var sections = new List<DocSection>();
        var stack = new List<(int Level, string Title)>();
        var body = new StringBuilder();
        string heading = fileName;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        string? fence = null;

        void Flush()
        {
            if (body.Length == 0 && heading == fileName) return;
            counts.TryGetValue(heading, out var n);
            counts[heading] = n + 1;
            sections.Add(new DocSection(heading, body.ToString().Trim('\n'), n));
            body.Clear();
        }

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (fence is null && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
                fence = trimmed[..3];
            else if (fence is not null && trimmed.StartsWith(fence, StringComparison.Ordinal))
                fence = null;
            else if (fence is null && Heading().Match(line) is { Success: true } m)
            {
                Flush();
                var level = m.Groups[1].Length;
                stack.RemoveAll(h => h.Level >= level);
                stack.Add((level, m.Groups[2].Value.Trim()));
                heading = string.Join(" › ", stack.Select(h => h.Title));
                continue;
            }
            body.Append(line).Append('\n');
        }
        Flush();
        return sections;
    }

    /// <summary>Stable across edits to the text: file, heading path, and which occurrence.</summary>
    public static string SectionId(string docPath, DocSection section) =>
        "d-" + Hash(docPath.ToLowerInvariant() + "\n" + section.Heading + "\n" + section.Ordinal)[..16];

    private static IEnumerable<(string Full, string Rel)> MarkdownFiles(string rootFull)
    {
        var pending = new Stack<string>();
        pending.Push(rootFull);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> subdirs, files;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir, "*.md");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }

            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                yield return (file, Relative(rootFull, file));

            foreach (var sub in subdirs.OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || SkippedDirs.Contains(name)) continue;
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;   // no symlink loops
                pending.Push(sub);
            }
        }
    }

    private static string Relative(string rootFull, string path) => Path.GetRelativePath(rootFull, path).Replace('\\', '/');

    private static string Normalize(string content) => content.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string Hash(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"\b(must|never|always|do not|don't|should not|shouldn't|required|avoid|make sure|only use)\b|반드시|금지|하지 ?말|하지 않는다|할 것|해야 한다|해야 합니다|않도록|주의|필수")]
    private static partial Regex Imperative();

    [GeneratedRegex(@"rule|guideline|convention|policy|checklist|do's|don'ts|pitfall|규칙|지침|원칙|규약|주의|금지|체크리스트")]
    private static partial Regex RuleHeading();
}
