using System.Text.RegularExpressions;
using AgentOne.Llm;

namespace AgentOne.Agent;

/// <summary>
/// The hand-off to the stronger model: one completion, no tools, given the
/// request, everything the tools gathered and the everyday model's draft. What
/// comes back is not shown as the answer — it goes back into the everyday
/// model's conversation as material, and that model writes the final answer.
/// The loop, the tone and the language stay the everyday model's; only the
/// thinking is borrowed.
/// </summary>
public static partial class ReasoningSubtask
{
    /// <summary>The step name the hand-off is reported under.</summary>
    public const string Tag = "reasoning";

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
        var received = 0;
        var reply = await reasoning.CompleteAsync(
            [ChatMessage.System(SystemPrompt(basicModel)), ChatMessage.User(Request(request, material, draft))],
            ct,
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
}
