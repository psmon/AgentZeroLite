using System.Text.RegularExpressions;
using AgentOne.Llm;

namespace AgentOne.Agent;

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
        "Plain prose or markdown — no JSON. Do not mention the assistant or this hand-off.";

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
    public static string DesignFeedBack(string reasoningModel, string design) =>
        $"[{DesignTag}:{reasoningModel}] " + design +
        "\n\nThat is the design for the request above. Implement it now, step by step: create each file with " +
        "write_file, run the commands with run_command, and check the results before moving on. When it is " +
        "done, answer with what was created and how it was verified. Do not mention the models or this hand-off.";
}
