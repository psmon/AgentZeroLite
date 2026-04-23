using System.Diagnostics;
using Xunit.Abstractions;

namespace ZeroCommon.Tests;

[Trait("Category", "Llm")]
public sealed class LlamaSharpLocalLlmTests
{
    private readonly ITestOutputHelper _output;

    private static readonly string ModelPath =
        Environment.GetEnvironmentVariable("GEMMA_MODEL_PATH")
        ?? @"D:\Code\AI\GemmaNet\models\gemma-4-E4B-it-UD-Q4_K_XL.gguf";

    public LlamaSharpLocalLlmTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public async Task Native_dlls_are_copied_to_test_output()
    {
        var baseDir = AppContext.BaseDirectory;
        var cpuDll = Path.Combine(baseDir, "runtimes", "win-x64-cpu", "native", "llama.dll");
        var vulkanDll = Path.Combine(baseDir, "runtimes", "win-x64-vulkan", "native", "llama.dll");

        _output.WriteLine($"CPU DLL:    {cpuDll} (exists={File.Exists(cpuDll)})");
        _output.WriteLine($"Vulkan DLL: {vulkanDll} (exists={File.Exists(vulkanDll)})");

        Assert.True(File.Exists(cpuDll), $"CPU llama.dll missing at {cpuDll}");
        Assert.True(File.Exists(vulkanDll), $"Vulkan llama.dll missing at {vulkanDll}");
    }

    [SkippableFact]
    public async Task Cpu_loads_model_and_completes_nonempty_response()
    {
        Skip.IfNot(File.Exists(ModelPath), $"Model not present at {ModelPath}");

        var opts = new LocalLlmOptions
        {
            ModelPath = ModelPath,
            Backend = LocalLlmBackend.Cpu,
            ContextSize = 2048,
            MaxTokens = 32,
            Temperature = 0.2f
        };

        var sw = Stopwatch.StartNew();
        await using var llm = await LlamaSharpLocalLlm.CreateAsync(opts);
        var loadMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var reply = await llm.CompleteAsync("Say the single word: hello");
        var genMs = sw.ElapsedMilliseconds;

        _output.WriteLine($"load={loadMs}ms gen={genMs}ms reply={reply.Trim()}");

        Assert.False(string.IsNullOrWhiteSpace(reply), "model produced empty response");
    }

    [SkippableFact]
    public async Task Cpu_streams_tokens_incrementally()
    {
        Skip.IfNot(File.Exists(ModelPath), $"Model not present at {ModelPath}");

        var opts = new LocalLlmOptions
        {
            ModelPath = ModelPath,
            Backend = LocalLlmBackend.Cpu,
            ContextSize = 2048,
            MaxTokens = 24,
            Temperature = 0.2f
        };

        await using var llm = await LlamaSharpLocalLlm.CreateAsync(opts);

        var tokenCount = 0;
        var total = new System.Text.StringBuilder();
        await foreach (var tok in llm.StreamAsync("Count: one, two,"))
        {
            tokenCount++;
            total.Append(tok);
            if (tokenCount <= 5)
                _output.WriteLine($"tok[{tokenCount}] = {tok}");
        }

        _output.WriteLine($"total tokens: {tokenCount}, text: {total}");
        Assert.True(tokenCount > 0, "no tokens streamed");
    }

    [SkippableFact]
    public async Task Cpu_function_call_invokes_declared_tool()
    {
        Skip.IfNot(File.Exists(ModelPath), $"Model not present at {ModelPath}");

        var opts = new LocalLlmOptions
        {
            ModelPath = ModelPath,
            Backend = LocalLlmBackend.Cpu,
            ContextSize = 2048,
            MaxTokens = 96,
            Temperature = 0.1f
        };

        // Gemma 4 permits tool declaration inside the user turn; no separate system role.
        // The expected reply format is a python-style call fenced in a tool_code block,
        // which is Gemma's canonical tool-call convention.
        var prompt = """
            You have exactly one function available:

              def get_weather(city: str) -> dict   # Return current weather for a city

            When the user asks about weather, reply ONLY with a Python call to this function
            wrapped in a ```tool_code fenced block. Do not add prose.

            User question: What is the weather in Tokyo right now?
            """;

        await using var llm = await LlamaSharpLocalLlm.CreateAsync(opts);

        var reply = await llm.CompleteAsync(prompt);
        _output.WriteLine($"reply:\n{reply}");

        Assert.Contains("get_weather", reply, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tokyo", reply, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Cpu_function_call_emits_structured_json_args()
    {
        Skip.IfNot(File.Exists(ModelPath), $"Model not present at {ModelPath}");

        var opts = new LocalLlmOptions
        {
            ModelPath = ModelPath,
            Backend = LocalLlmBackend.Cpu,
            ContextSize = 2048,
            MaxTokens = 128,
            Temperature = 0.1f
        };

        // Force JSON-only output so the reply is machine-parseable — this is the
        // pattern most C# agent hosts use regardless of the model's native format.
        var prompt = """
            You have exactly one function:

              name: create_reminder
              args: { title: string, when_iso: string (ISO-8601) }

            Reply with ONLY a single JSON object matching:
              {"tool":"create_reminder","args":{"title":"...","when_iso":"..."}}

            No markdown, no prose, no trailing text.

            User: Remind me to submit the expense report tomorrow at 9am.
            """;

        await using var llm = await LlamaSharpLocalLlm.CreateAsync(opts);

        var reply = (await llm.CompleteAsync(prompt)).Trim();
        _output.WriteLine($"reply:\n{reply}");

        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        Assert.True(start >= 0 && end > start, $"no JSON object found in reply: {reply}");

        var json = reply[start..(end + 1)];
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("create_reminder", root.GetProperty("tool").GetString());
        var args = root.GetProperty("args");
        var title = args.GetProperty("title").GetString() ?? "";
        Assert.Contains("expense", title, StringComparison.OrdinalIgnoreCase);
        Assert.True(args.TryGetProperty("when_iso", out _), "args.when_iso missing");
    }
}
