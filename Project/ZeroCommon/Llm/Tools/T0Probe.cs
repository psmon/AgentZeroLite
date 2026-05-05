using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Agent.Common.Llm.Tools;

/// <summary>
/// T0 — runtime probe per <c>harness/knowledge/ondevice-tool-calling-survey.md</c>.
/// Asks a loaded model a tool-use question with NO grammar enforcement, then
/// inspects the raw output for the model family's native tool-call markers
/// (Llama-3.1 → <c>&lt;|python_tag|&gt;</c> / <c>&lt;|eom_id|&gt;</c>;
/// Gemma → none expected). The result tells the dual-backend strategy
/// whether <c>NativeToolBackend</c> is viable for the loaded model or whether
/// the loop must route through <see cref="LocalAgentLoop"/> (GBNF).
///
/// Unlike <see cref="LocalAgentLoop"/> this probe is intentionally
/// grammar-free — that is the entire point of the test. It is also single
/// turn and stateless (uses <see cref="StatelessExecutor"/> directly), so it
/// can run cheaply in the test harness alongside the model load already
/// performed for proof-of-life tests.
/// </summary>
public sealed record T0ProbeResult(
    string ModelFamilyId,
    string PromptUsed,
    string RawOutput,
    bool EmittedNativeToolMarkers,
    IReadOnlyList<string> DetectedMarkers,
    string Recommendation);

public static class T0Probe
{
    // Markers that, when present in the raw model output, indicate the
    // model's SFT path emitted native tool-call tokens for that family.
    private static readonly Dictionary<string, string[]> NativeMarkersByFamily =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Llama-3.1 SFT (used by Nemotron Nano 8B-v1) emits function calls
            // wrapped in these tokens per Meta's tool-use prompt format.
            ["llama31"] = new[] { "<|python_tag|>", "<|eom_id|>" },
            // Gemma has no tool-calling SFT; these markers should NEVER appear
            // in healthy Gemma output. If any do, it is a tokenizer regression
            // worth investigating.
            ["gemma"] = new[] { "<|python_tag|>", "<|eom_id|>", "<tool_call>", "<|tool|>" }
        };

    public static async Task<T0ProbeResult> RunAsync(
        LlamaSharpLocalLlm llm,
        ChatTemplate template,
        CancellationToken ct = default)
    {
        var (weights, modelParams) = llm.GetInternals();
        var executor = new StatelessExecutor(weights, modelParams);

        var prompt = BuildPrompt(template);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = 96,
            AntiPrompts = template.AntiPrompts,
            // No Grammar — that is the whole point of T0.
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.1f }
        };

        var sb = new StringBuilder();
        await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct))
            sb.Append(token);
        var raw = sb.ToString();

        var markers = NativeMarkersByFamily.TryGetValue(template.FamilyId, out var arr)
            ? arr
            : Array.Empty<string>();
        var detected = markers
            .Where(m => raw.Contains(m, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var nativeViable = detected.Count > 0;

        return new T0ProbeResult(
            ModelFamilyId: template.FamilyId,
            PromptUsed: prompt,
            RawOutput: raw,
            EmittedNativeToolMarkers: nativeViable,
            DetectedMarkers: detected,
            Recommendation: nativeViable
                ? $"NativeToolBackend viable for '{template.FamilyId}'."
                : $"Use LocalAgentLoop (GBNF) for '{template.FamilyId}' — model does not emit native tool tokens.");
    }

    private static string BuildPrompt(ChatTemplate template) => template.FamilyId switch
    {
        // Standard Llama-3.1 tool-use prompt format from Meta's spec. The
        // explicit "respond with <|python_tag|>...<|eom_id|>" instruction is
        // what triggers the SFT-trained tool-call path.
        "llama31" =>
            "<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\n"
            + "Environment: ipython\n"
            + "Tools: list_terminals\n\n"
            + "You have access to the following function:\n"
            + "  list_terminals() -> str   \"List currently open terminals\"\n\n"
            + "When you decide to call a function, respond in this exact format:\n"
            + "<|python_tag|>list_terminals()<|eom_id|>\n\n"
            + "Otherwise reply normally."
            + "<|eot_id|><|start_header_id|>user<|end_header_id|>\n\n"
            + "What terminals are open right now?<|eot_id|>"
            + "<|start_header_id|>assistant<|end_header_id|>\n\n",

        // Gemma has no native tool path. Use the family's normal chat
        // template and let the model produce whatever it would naturally —
        // we expect prose and/or pseudo-JSON, NOT Llama-style tokens.
        _ => template.Format(
            "You have one function:\n"
          + "  list_terminals() -> str\n\n"
          + "Call this function to find out which terminals are open. "
          + "Reply with just the function call — no prose, no markdown.",
            isFirstTurn: true)
    };
}
