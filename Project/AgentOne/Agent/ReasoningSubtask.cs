using System.Text.RegularExpressions;
using AgentOne.Llm;

namespace AgentOne.Agent;

/// <summary>A choice the strong model's design hinges on, put to the person before building.</summary>
/// <param name="Recommended">Index into <paramref name="Options"/> of the one the design recommends, or 0.</param>
public sealed record DesignDecision(IReadOnlyList<string> Options, int Recommended);

/// <summary>
/// The hand-offs to the stronger model: one completion each, no tools. Two
/// kinds — a <b>reasoning</b> pass after the everyday model's draft, and a
/// <b>design</b> pass before it starts building something large. In both, what
/// comes back is not shown as the answer: it goes back into the everyday
/// model's conversation as material, and that model does the writing or the
/// building. The loop, the tone and the language stay the everyday model's;
/// only the thinking is borrowed.
/// </summary>
public static partial class ReasoningSubtask
{
    /// <summary>The step name the reasoning hand-off is reported under.</summary>
    public const string Tag = "reasoning";

    /// <summary>The step name the design hand-off is reported under.</summary>
    public const string DesignTag = "design";

    /// <summary>The line a design starts with when the person has to choose first.</summary>
    public const string DecisionMarker = "DECISION NEEDED";

    public static string SystemPrompt(string basicModel) =>
        "You are the stronger reasoning model behind a small, fast assistant " +
        $"({basicModel}). The assistant drafted an answer and gathered material with read-only tools; both follow. " +
        "Think the problem through carefully and write a complete, well-reasoned answer in the user's language. " +
        "Plain prose or markdown — no JSON, no tool calls. Where the material is insufficient, say what is missing " +
        "rather than guessing. Do not mention the assistant, the draft, or this hand-off.";

    public static string Request(string request, string material, string draft) =>
        "Request:\n" + request +
        "\n\nMaterial gathered by tools:\n" + (material.Length == 0 ? "(none)" : material) +
        "\n\nDraft answer by the assistant:\n" + draft;

    /// <param name="onProgress">
    /// Characters received so far, as they arrive. The call is always streamed:
    /// a strong model thinking for a minute sends nothing otherwise, and a
    /// gateway in between closes a silent connection (measured: HTTP 504 at
    /// 90 s from one). Bytes on the wire keep it open, and the count keeps the
    /// person watching from thinking it has hung.
    /// </param>
    public static async Task<string> RunAsync(
        IChatProvider reasoning, string basicModel, string request, string material, string draft,
        CancellationToken ct, Action<int>? onProgress = null)
    {
        return await CompleteAsync(reasoning,
            [ChatMessage.System(SystemPrompt(basicModel)), ChatMessage.User(Request(request, material, draft))],
            ct, onProgress);
    }

    // ------------------------------------------------------------- design

    public static string DesignSystemPrompt(string basicModel, string shell) =>
        "You are the stronger model designing work that a small, fast assistant " +
        $"({basicModel}) will then carry out with these tools: read/list/find/grep files, write_file(path, content), " +
        $"and run_command (one {shell} command, in the project folder). " +
        "Produce an implementation design, not the implementation: the file layout (every path relative to the " +
        "project root), what each file is responsible for, the commands to run and in what order, and the checks " +
        "that prove it works. Be concrete and short — a numbered list of steps the assistant can follow one by one. " +
        "Include code only where a signature or a config line must be exact. Write in the user's language. " +
        "Plain prose or markdown — no JSON. Do not mention the assistant or this hand-off. " +
        $"If the design hinges on a choice only the user can make — a framework, a storage engine, a structure — begin " +
        $"with one line \"{DecisionMarker}:\" followed by the options as a numbered list, one per line, each a short " +
        "phrase, with \"(recommended)\" after the one you would pick; then write the design for the recommended one. " +
        "If there is no such choice, do not write that line.";

    public static string DesignRequest(string request, string context) =>
        "Request:\n" + request +
        (context.Length == 0 ? "" : "\n\nWhat is already known about the project:\n" + context);

    /// <summary>The design pass: the strong model plans, the everyday model will build.</summary>
    public static async Task<string> DesignAsync(
        IChatProvider reasoning, string basicModel, string shell, string request, string context,
        CancellationToken ct, Action<int>? onProgress = null)
    {
        return await CompleteAsync(reasoning,
            [ChatMessage.System(DesignSystemPrompt(basicModel, shell)), ChatMessage.User(DesignRequest(request, context))],
            ct, onProgress);
    }

    /// <summary>
    /// The choice at the top of a design, if the strong model put one there, and
    /// the design without it. Null when there is nothing to decide.
    /// </summary>
    public static DesignDecision? ExtractDecision(string design, out string rest)
    {
        rest = design;
        var lines = design.Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimStart().StartsWith(DecisionMarker, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return null;

        var options = new List<string>();
        var recommended = -1;
        var end = start + 1;

        for (; end < lines.Length; end++)
        {
            var line = lines[end].Trim();
            if (line.Length == 0) { if (options.Count > 0) break; continue; }

            var match = NumberedLine().Match(line);
            if (!match.Success) break;

            var text = match.Groups[1].Value.Trim();
            if (text.Contains("(recommended)", StringComparison.OrdinalIgnoreCase))
            {
                recommended = options.Count;
                text = Recommended().Replace(text, "").Trim();
            }
            options.Add(text);
        }

        if (options.Count < 2) return null;

        rest = string.Join('\n', lines.Take(start).Concat(lines.Skip(end))).Trim();
        return new DesignDecision(options, Math.Max(0, recommended));
    }

    [GeneratedRegex(@"^\s*(?:\d+[.)]|[-*])\s+(.+)$")]
    private static partial Regex NumberedLine();

    [GeneratedRegex(@"\s*\(recommended\)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Recommended();

    /// <summary>The design's first lines, for a person following along on screen.</summary>
    public static IReadOnlyList<string> Summary(string design, int maxLines = 14, int maxColumns = 110)
    {
        var shown = new List<string>();
        var total = 0;
        foreach (var raw in design.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) continue;
            total++;
            if (shown.Count >= maxLines) continue;
            shown.Add(line.Length > maxColumns ? line[..maxColumns] + "…" : line);
        }
        if (total > shown.Count) shown.Add($"… {total - shown.Count} more lines (the model has the whole design)");
        return shown;
    }

    private static async Task<string> CompleteAsync(
        IChatProvider provider, ChatMessage[] messages, CancellationToken ct, Action<int>? onProgress)
    {
        var received = 0;
        var reply = await provider.CompleteAsync(messages, ct,
            fragment => { received += fragment.Length; onProgress?.Invoke(received); });

        return StripThinking(reply).Trim();
    }

    /// <summary>
    /// Reasoning models that show their work wrap it in &lt;think&gt; tags. That
    /// part is for them; the everyday model gets the conclusion. An unclosed
    /// block (the reply was cut off) is dropped too, rather than handed over as
    /// half a thought.
    /// </summary>
    public static string StripThinking(string reply)
    {
        var stripped = ThinkBlock().Replace(reply, "");
        var open = stripped.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        return open >= 0 ? stripped[..open] : stripped;
    }

    [GeneratedRegex(@"<think>.*?</think>\s*", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    /// <summary>
    /// The line that carries the stronger model's answer back into the everyday
    /// model's conversation. Tagged like a tool result, because that is what it
    /// is to that model: material, handed to it, to answer from.
    /// </summary>
    public static string FeedBack(string reasoningModel, string reasoning) =>
        $"[{Tag}:{reasoningModel}] " + reasoning +
        "\n\nThat is the stronger model's analysis of the request above. Using it, write the final answer for " +
        "the user, in their language. Do not mention the models or this hand-off — just answer.";

    /// <summary>The design, handed to the everyday model with the order to build it.</summary>
    /// <param name="decision">What the person chose, when the design hinged on a choice; null otherwise.</param>
    public static string DesignFeedBack(string reasoningModel, string design, string? decision = null) =>
        $"[{DesignTag}:{reasoningModel}] " + design +
        (decision is { Length: > 0 } ? $"\n\nThe user decided: {decision}. Follow that choice wherever the design offered alternatives." : "") +
        "\n\nThat is the design for the request above. Implement it now, step by step: create each file with " +
        "write_file, run the commands with run_command, and check the results before moving on. When it is " +
        "done, answer with what was created, what was verified, what is left, and the suggested next steps. " +
        "Do not mention the models or this hand-off.";
}
