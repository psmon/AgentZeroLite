using System.Text;
using System.Text.RegularExpressions;

namespace AgentOne.Agent;

/// <summary>The workspace's own instructions for agents, as read for one session.</summary>
/// <param name="Files">The files it came from, relative to the root.</param>
/// <param name="Text">What goes in the prompt — cut to a budget, with an outline of what was cut.</param>
public sealed record WorkspaceGuideText(IReadOnlyList<string> Files, string Text);

/// <summary>
/// AGENTS.md / CLAUDE.md — the file a project keeps for whatever agent works in
/// it. Measured (2026-11-06, music-harness): asked "종말의 그날 1,2 를 통합해
/// 뮤비작성을 진행해보자", agent-one never opened AGENTS.md, whose dispatch table
/// sends "뮤비 만들어" to harness/engine/mv-forge.md. The strong model designed a
/// generic "join two local videos with ffmpeg" plan for 493 s, the build looked
/// for files it had invented, and the turn ended asking the person to supply
/// them. Claude Code and Codex read this file on every session; so does agent-one now.
///
/// It is cut to a budget, not dropped: the head (where such files put their
/// most important rule and dispatch table) goes in whole, and the headings of
/// the rest are listed so the model knows what to read_file for.
/// </summary>
public static partial class WorkspaceGuide
{
    /// <summary>Looked for at the root, in this order; every one found is used.</summary>
    public static readonly string[] FileNames = ["AGENTS.md", "CLAUDE.md", "AGENT.md", ".claude/CLAUDE.md"];

    /// <summary>The share of the system prompt the guide may take.</summary>
    public const int PromptChars = 12_000;

    /// <summary>The share of the strong model's design request.</summary>
    public const int DesignChars = 6_000;

    /// <summary>Larger files are not read at all — a guide that size is not a guide.</summary>
    private const long MaxFileBytes = 256 * 1024;

    public static WorkspaceGuideText? Load(string root, int maxChars = PromptChars)
    {
        var found = new List<(string Name, string Text)>();
        foreach (var name in FileNames)
        {
            var path = Path.Combine(root, name);
            string text;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxFileBytes) continue;
                text = File.ReadAllText(path).Replace("\r\n", "\n").Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            // A CLAUDE.md that only imports AGENTS.md (`@AGENTS.md`) adds nothing,
            // and the same text twice is twice the budget for nothing.
            if (Substance(text) < 40 || found.Any(f => f.Text == text)) continue;
            found.Add((name, text));
        }

        if (found.Count == 0) return null;

        var sb = new StringBuilder();
        var left = maxChars;
        foreach (var (name, text) in found)
        {
            if (left < 400) { sb.Append("\n── ").Append(name).Append(" ── (not shown: read_file ").Append(name).AppendLine(")"); continue; }
            var body = Cut(text, name, left);
            sb.Append("── ").Append(name).AppendLine(" ──").AppendLine(body);
            left -= body.Length;
        }

        return new WorkspaceGuideText(found.Select(f => f.Name).ToList(), sb.ToString().TrimEnd());
    }

    /// <summary>
    /// The text up to <paramref name="max"/> characters, cut at a heading or a
    /// blank line, plus the headings that follow the cut.
    /// </summary>
    internal static string Cut(string text, string name, int max)
    {
        if (text.Length <= max) return text;

        // Leave room for the outline.
        var budget = Math.Max(200, max - 900);
        var cut = text.LastIndexOf("\n#", budget, StringComparison.Ordinal);
        if (cut < budget / 2) cut = text.LastIndexOf("\n\n", budget, StringComparison.Ordinal);
        if (cut < budget / 2) cut = budget;

        var rest = Outline(text[cut..]);
        var sb = new StringBuilder(text[..cut].TrimEnd());
        sb.Append($"\n… {name} is cut here ({cut:N0} of {text.Length:N0} chars).");
        if (rest.Count > 0)
            sb.Append(" The rest covers: ").Append(string.Join(" · ", rest)).Append('.');
        sb.Append($" read_file {name} for any of it.");
        return sb.ToString();
    }

    /// <summary>Markdown headings (levels 1–3) outside code fences, as they read.</summary>
    internal static IReadOnlyList<string> Outline(string text, int max = 30)
    {
        var headings = new List<string>();
        var fenced = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { fenced = !fenced; continue; }
            if (fenced) continue;
            var m = Heading().Match(line);
            if (!m.Success) continue;
            headings.Add(m.Groups[1].Value.Trim());
            if (headings.Count >= max) break;
        }
        return headings;
    }

    private static int Substance(string text) =>
        text.Split('\n').Where(l => !l.TrimStart().StartsWith('@')).Sum(l => l.Trim().Length);

    [GeneratedRegex(@"^#{1,3}\s+(.+)$")]
    private static partial Regex Heading();
}
