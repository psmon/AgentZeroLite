namespace Agent.Common.Llm.Tools;

/// <summary>
/// Chat-template specifics for one model family. The Tools loop
/// (<see cref="AgentToolLoop"/>) is otherwise model-agnostic; only the prompt
/// markers and anti-prompts differ between Gemma and Llama-3.x-derived models
/// (Nemotron Nano).
///
/// Both templates inline the system prompt into the first user turn (rather
/// than using the model's "system" role). This keeps `FormatFirstTurn` /
/// `FormatToolResultTurn` in <see cref="AgentToolLoop"/> identical across
/// templates and lets the Gemma path stay untouched (Gemma has no system
/// role anyway).
/// </summary>
public sealed record ChatTemplate(
    string FirstTurnFormat,
    string ContinuationFormat,
    IReadOnlyList<string> AntiPrompts,
    string FamilyId)
{
    /// <summary>
    /// Wraps <paramref name="content"/> with the family's chat markers.
    /// </summary>
    public string Format(string content, bool isFirstTurn)
        => string.Format(isFirstTurn ? FirstTurnFormat : ContinuationFormat, content);
}

public static class ChatTemplates
{
    /// <summary>
    /// Google Gemma 2/3/4 chat template. No system role — system prompt is
    /// inlined into the user turn at the application layer.
    /// </summary>
    public static readonly ChatTemplate Gemma = new(
        FirstTurnFormat:
            "<start_of_turn>user\n{0}<end_of_turn>\n<start_of_turn>model\n",
        ContinuationFormat:
            "\n<start_of_turn>user\n{0}<end_of_turn>\n<start_of_turn>model\n",
        AntiPrompts: new[] { "<end_of_turn>", "<eos>" },
        FamilyId: "gemma");

    /// <summary>
    /// Meta Llama-3.1 / Llama-3.2 / NVIDIA Nemotron Nano (Llama-3.1 backbone)
    /// chat template. Uses `&lt;|start_header_id|&gt;…&lt;|end_header_id|&gt;`
    /// per-turn role headers and `&lt;|eot_id|&gt;` end-of-turn marker.
    ///
    /// **No leading `&lt;|begin_of_text|&gt;`** — LLamaSharp / llama.cpp
    /// auto-prepends the model's BOS token when it tokenizes the prompt, so
    /// embedding `&lt;|begin_of_text|&gt;` here would produce a doubled BOS
    /// (observed warning: <c>"Added a BOS token... prompt also starts with
    /// a BOS token. So now the final prompt starts with 2 BOS tokens"</c>),
    /// degrading first-turn quality and tool-call JSON formation.
    ///
    /// We deliberately omit the system role and inline system+user content
    /// in a single user turn. This keeps the application layer
    /// (FormatFirstTurn etc.) shared across templates. Per the Nemotron
    /// Nano model card, system role is supported but optional, and inline
    /// system content works identically.
    /// </summary>
    public static readonly ChatTemplate Llama31 = new(
        FirstTurnFormat:
            "<|start_header_id|>user<|end_header_id|>\n\n" +
            "{0}<|eot_id|>" +
            "<|start_header_id|>assistant<|end_header_id|>\n\n",
        ContinuationFormat:
            "<|start_header_id|>user<|end_header_id|>\n\n" +
            "{0}<|eot_id|>" +
            "<|start_header_id|>assistant<|end_header_id|>\n\n",
        AntiPrompts: new[] { "<|eot_id|>", "<|end_of_text|>" },
        FamilyId: "llama31");
}
