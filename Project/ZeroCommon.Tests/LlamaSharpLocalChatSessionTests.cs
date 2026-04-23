using System.Diagnostics;
using Xunit.Abstractions;

namespace ZeroCommon.Tests;

[Trait("Category", "Llm")]
public sealed class LlamaSharpLocalChatSessionTests
{
    private readonly ITestOutputHelper _output;

    private static readonly string ModelPath =
        Environment.GetEnvironmentVariable("GEMMA_MODEL_PATH")
        ?? @"D:\Code\AI\GemmaNet\models\gemma-4-E4B-it-UD-Q4_K_XL.gguf";

    public LlamaSharpLocalChatSessionTests(ITestOutputHelper output) => _output = output;

    private static LocalLlmOptions Opts() => new()
    {
        ModelPath = ModelPath,
        Backend = LocalLlmBackend.Cpu,
        ContextSize = 2048,
        MaxTokens = 48,
        Temperature = 0.1f
    };

    [SkippableFact]
    public async Task Session_remembers_earlier_turn()
    {
        Skip.IfNot(File.Exists(ModelPath), $"Model not present at {ModelPath}");

        await using var llm = await LlamaSharpLocalLlm.CreateAsync(Opts());
        await using var session = llm.CreateSession();

        var sw1 = Stopwatch.StartNew();
        var r1 = await session.SendAsync("My name is Cheolsu. Just say OK if you understand.");
        sw1.Stop();

        var sw2 = Stopwatch.StartNew();
        var r2 = await session.SendAsync("What name did I just give you? Reply with ONLY the name, nothing else.");
        sw2.Stop();

        _output.WriteLine($"turn1 ({sw1.ElapsedMilliseconds}ms): {r1.Trim()}");
        _output.WriteLine($"turn2 ({sw2.ElapsedMilliseconds}ms): {r2.Trim()}");
        _output.WriteLine($"TurnCount={session.TurnCount}");

        Assert.Equal(2, session.TurnCount);
        Assert.Contains("Cheolsu", r2, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Two_sessions_have_isolated_history()
    {
        Skip.IfNot(File.Exists(ModelPath), $"Model not present at {ModelPath}");

        await using var llm = await LlamaSharpLocalLlm.CreateAsync(Opts());
        await using var sessionA = llm.CreateSession();
        await using var sessionB = llm.CreateSession();

        await sessionA.SendAsync("The magic code word is ORANGE. Remember it.");
        await sessionB.SendAsync("The magic code word is PURPLE. Remember it.");

        var aAnswer = await sessionA.SendAsync("What was the magic code word? Reply with only the word.");
        var bAnswer = await sessionB.SendAsync("What was the magic code word? Reply with only the word.");

        _output.WriteLine($"A: {aAnswer.Trim()}");
        _output.WriteLine($"B: {bAnswer.Trim()}");

        Assert.Contains("ORANGE", aAnswer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PURPLE", bAnswer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PURPLE", aAnswer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ORANGE", bAnswer, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Second_turn_uses_kv_cache_and_is_not_slower_than_first()
    {
        Skip.IfNot(File.Exists(ModelPath), $"Model not present at {ModelPath}");

        await using var llm = await LlamaSharpLocalLlm.CreateAsync(Opts());
        await using var session = llm.CreateSession();

        // Feed a long prefix in turn 1 so prefill cost is meaningful, then measure turn 2.
        var longPrefix = string.Join(" ", Enumerable.Range(0, 40)
            .Select(i => $"Fact {i}: the code name at index {i} is token_{i}."));

        var sw1 = Stopwatch.StartNew();
        await session.SendAsync($"{longPrefix}\n\nPlease just reply OK.");
        var t1 = sw1.ElapsedMilliseconds;

        var sw2 = Stopwatch.StartNew();
        var r2 = await session.SendAsync("Reply with a single word: done");
        var t2 = sw2.ElapsedMilliseconds;

        _output.WriteLine($"turn1(with prefill of long prefix)={t1}ms, turn2(short)={t2}ms, reply2={r2.Trim()}");

        // KV cache retention should make a short second turn significantly cheaper
        // than the first turn that had to prefill the long prefix.
        Assert.True(t2 < t1, $"expected KV-reuse speedup: turn2({t2}ms) should be < turn1({t1}ms)");
    }
}
