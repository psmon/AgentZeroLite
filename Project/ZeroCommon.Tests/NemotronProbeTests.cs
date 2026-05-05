using System.Diagnostics;
using System.Text;
using Agent.Common.Llm;
using Agent.Common.Llm.Tools;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Xunit.Abstractions;

namespace ZeroCommon.Tests;

/// <summary>
/// Sanity probes for the recently-added Nemotron Nano 8B v1 GGUF — verifies
/// that the model loads in our LLamaSharp 0.26 + llama.cpp commit
/// 3f7c29d stack and produces a token. Run BEFORE writing the
/// NemotronAgentLoop backend so we don't sink time into a backend that
/// can't even load.
///
/// Same Skip-if-no-model gating pattern as LlamaSharpLocalChatSessionTests.
/// CPU backend per maintainer direction.
///
/// Note: these tests use the **Llama-3.1 chat template** (not Gemma's
/// `&lt;start_of_turn&gt;` markers) because that's Nemotron's native format.
/// We bypass `LlamaSharpLocalLlm.StreamAsync` (which Gemma-wraps the prompt)
/// and use `StatelessExecutor` directly via the same-assembly internal
/// accessor. Anti-prompts are Llama-3.1's `&lt;|eot_id|&gt;` /
/// `&lt;|end_of_text|&gt;`.
/// </summary>
[Trait("Category", "Llm")]
[Trait("Backend", "Cpu")]
[Trait("Model", "Nemotron-Nano-8B-v1")]
public sealed class NemotronProbeTests
{
    private readonly ITestOutputHelper _output;

    private static readonly string ModelPath =
        Environment.GetEnvironmentVariable("NEMOTRON_MODEL_PATH")
        ?? @"D:\Code\AI\GemmaNet\models\Llama-3.1-Nemotron-Nano-8B-v1-UD-Q4_K_XL.gguf";

    private static readonly string[] Llama31AntiPrompts = { "<|eot_id|>", "<|end_of_text|>" };

    public NemotronProbeTests(ITestOutputHelper output) => _output = output;

    private static LocalLlmOptions Opts() => new()
    {
        ModelPath = ModelPath,
        Backend = LocalLlmBackend.Cpu,
        ContextSize = 2048,
        MaxTokens = 32,
        Temperature = 0.1f,
    };

    private static string BuildLlama31Prompt(string systemPrompt, string userMessage)
        => "<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\n"
         + systemPrompt
         + "<|eot_id|>"
         + "<|start_header_id|>user<|end_header_id|>\n\n"
         + userMessage
         + "<|eot_id|>"
         + "<|start_header_id|>assistant<|end_header_id|>\n\n";

    /// <summary>
    /// Bare-minimum proof of life: load Nemotron, ask for one word, get one
    /// word back. If this fails, LocalAgentLoop work for Nemotron is blocked
    /// until the load issue is resolved (could be llama.cpp commit support
    /// gap, GGUF format issue, etc.).
    /// </summary>
    [SkippableFact]
    public async Task Cpu_load_and_single_token_via_llama31_template()
    {
        Skip.IfNot(File.Exists(ModelPath), $"Model not present at {ModelPath}");

        var sw = Stopwatch.StartNew();
        await using var llm = await LlamaSharpLocalLlm.CreateAsync(Opts());
        var loadMs = sw.ElapsedMilliseconds;

        // Direct StatelessExecutor with the raw Llama-3.1 template (bypass
        // LlamaSharpLocalLlm.StreamAsync which Gemma-wraps the prompt).
        var (weights, modelParams) = llm.GetInternals();
        var executor = new StatelessExecutor(weights, modelParams);

        var prompt = BuildLlama31Prompt(
            systemPrompt: "You are a helpful assistant.",
            userMessage: "Reply with only the single word: hello");

        var inferenceParams = new InferenceParams
        {
            MaxTokens = 16,
            AntiPrompts = Llama31AntiPrompts,
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.1f },
        };

        sw.Restart();
        var sb = new StringBuilder();
        await foreach (var token in executor.InferAsync(prompt, inferenceParams))
            sb.Append(token);
        var inferMs = sw.ElapsedMilliseconds;

        var reply = sb.ToString().Trim();
        _output.WriteLine($"load={loadMs}ms infer={inferMs}ms reply.len={reply.Length}");
        _output.WriteLine($"reply: {reply.Replace("\n", "\\n")}");

        Assert.False(string.IsNullOrWhiteSpace(reply), "expected non-empty reply");
        // Soft semantic check — at temp 0.1 on a "say hello" prompt the
        // model should produce something containing "hello".
        Assert.Contains("hello", reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// T0Native — runtime probe per ondevice-tool-calling-survey.md §B.
    /// Gives Nemotron Nano 8B-v1 a Llama-3.1 tool-use prompt with NO grammar
    /// enforcement and inspects the raw output for native function-call
    /// markers (`&lt;|python_tag|&gt;` / `&lt;|eom_id|&gt;`).
    ///
    /// Empirical result on our self-built stack (LLamaSharp 0.26 + llama.cpp
    /// commit 3f7c29d, Nemotron-Nano-8B-v1 UD-Q4_K_XL, CPU): the model
    /// produces a plain `list_terminals()` call rather than the SFT-trained
    /// `&lt;|python_tag|&gt;...&lt;|eom_id|&gt;` envelope. The survey's
    /// "Native primary" prediction does NOT hold for this GGUF/commit.
    /// Decision: route Nemotron through GBNF too (same as Gemma) — which is
    /// what the existing LocalAgentLoop already does via the Llama31 chat
    /// template injection. NativeToolBackend implementation deferred until
    /// either Nano-9B-v2 lands or a future llama.cpp commit changes the
    /// tokenizer behaviour for these tokens.
    ///
    /// Assertion direction: assert NO native markers (the observed reality).
    /// If a future stack update flips this to YES, the test fires and we
    /// know to revisit the NativeToolBackend decision.
    /// </summary>
    [SkippableFact]
    public async Task T0Native_Nemotron_does_not_emit_llama31_tool_tokens_on_current_stack()
    {
        Skip.IfNot(File.Exists(ModelPath), $"Model not present at {ModelPath}");

        await using var llm = await LlamaSharpLocalLlm.CreateAsync(Opts());
        var result = await T0Probe.RunAsync(llm, ChatTemplates.Llama31);

        _output.WriteLine($"family={result.ModelFamilyId} native_viable={result.EmittedNativeToolMarkers}");
        _output.WriteLine($"detected_markers=[{string.Join(", ", result.DetectedMarkers)}]");
        _output.WriteLine($"recommendation: {result.Recommendation}");
        var rawPreview = result.RawOutput.Length <= 400
            ? result.RawOutput.Replace("\n", "\\n")
            : result.RawOutput[..400].Replace("\n", "\\n") + "…";
        _output.WriteLine($"raw[..400]: {rawPreview}");

        Assert.False(result.EmittedNativeToolMarkers,
            $"Nemotron unexpectedly emitted native Llama-3.1 tool tokens "
            + $"(detected: [{string.Join(", ", result.DetectedMarkers)}]). "
            + "The current stack used to produce plain `list_terminals()` text. "
            + "If this fires, native tool parsing is now viable on our llama.cpp commit — "
            + "consider implementing NativeToolBackend per ondevice-tool-calling-survey.md §architecture.");
    }
}
