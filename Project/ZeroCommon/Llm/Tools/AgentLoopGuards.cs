using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Agent.Common.Llm.Tools;

/// <summary>
/// Defends an agent loop against pathological LLM behavior. Owned by one
/// <see cref="LocalAgentLoop"/> or <see cref="ExternalAgentLoop"/> run —
/// never shared across loops because state (call counts, recent attempts)
/// is per-run.
///
/// Three concerns:
/// <list type="number">
///   <item><description><b>Repeat detection</b> — same (tool + normalized args)
///     called more than <c>MaxSameCallRepeats</c> times produces an error
///     toolResult that the loop feeds back to the LLM (2-stage defense:
///     give the model a chance to self-correct first).</description></item>
///   <item><description><b>Consecutive-block hard stop</b> — if the LLM ignores
///     the feedback <c>MaxConsecutiveBlocks</c> times in a row, abort.</description></item>
///   <item><description><b>Transient HTTP retry with exponential backoff</b> —
///     for REST-backed loops; classifies via <see cref="HttpRequestException.StatusCode"/>
///     when typed, falls back to message keywords. Origin's ReActActor uses
///     no backoff; we add 1s/3s/5s to avoid hammering a degraded gateway.</description></item>
/// </list>
///
/// Args are canonicalized (sorted keys + round-trip) before counting so
/// <c>{"a":1,"b":2}</c> and <c>{"b":2,"a":1}</c> hit the same counter —
/// closes a known loophole in Origin's raw-string keying.
/// </summary>
public sealed class AgentLoopGuards
{
    private const int RecentBufferSize = 5;
    private const int RecentArgsTruncate = 60;
    private const int RecentResultTruncate = 120;

    private readonly Dictionary<string, int> _callCounts = new(StringComparer.Ordinal);
    private readonly LinkedList<RecentAttempt> _recent = new();
    private int _consecutiveBlocks;
    private int _llmRetries;

    public int BlockedRepeats { get; private set; }
    public int LlmRetries => _llmRetries;
    public int ConsecutiveBlocks => _consecutiveBlocks;

    /// <summary>
    /// Tests whether this exact (tool, args) call would exceed
    /// <paramref name="maxSameCallRepeats"/>. Returns null when the call is
    /// allowed; returns a synthesized toolResult string when blocked. The
    /// blocked message includes a summary of the last 5 attempts so the
    /// model can choose a different approach with full context (Lite
    /// refinement over Origin's bare error text).
    ///
    /// Always increments the per-key counter — even when blocked — so a
    /// model that ignores the block message and tries again still trips
    /// the consecutive-blocks guard.
    /// </summary>
    public string? CheckRepeat(ToolCall call, int maxSameCallRepeats)
    {
        var key = MakeKey(call);
        _callCounts.TryGetValue(key, out var count);
        var newCount = count + 1;
        _callCounts[key] = newCount;

        if (newCount > maxSameCallRepeats)
        {
            BlockedRepeats++;
            _consecutiveBlocks++;
            return BuildBlockMessage(call, newCount);
        }

        // A non-blocked call clears the consecutive counter — the guard only
        // fires on *uninterrupted* runs of identical-call abuse. A model that
        // diversifies between repeats is not stuck.
        _consecutiveBlocks = 0;
        return null;
    }

    /// <summary>
    /// Records the actual tool result so the next block message (if any) can
    /// quote what the model already saw. Truncated to keep the feedback prompt
    /// from blowing up.
    /// </summary>
    public void RecordResult(ToolCall call, string toolResult)
    {
        _recent.AddLast(new RecentAttempt(
            call.Tool,
            Truncate(NormalizeArgsJson(call.Args), RecentArgsTruncate),
            Truncate(toolResult, RecentResultTruncate)));
        if (_recent.Count > RecentBufferSize)
            _recent.RemoveFirst();
    }

    /// <summary>True once <see cref="ConsecutiveBlocks"/> reaches the cap — caller breaks the loop.</summary>
    public bool ShouldHardStop(int maxConsecutiveBlocks)
        => _consecutiveBlocks >= maxConsecutiveBlocks;

    /// <summary>
    /// Tries to consume one transient-HTTP retry budget. Returns true if the
    /// caller may retry (and increments the retry counter); returns false when
    /// the budget is exhausted.
    /// </summary>
    public bool TryConsumeLlmRetry(int maxLlmRetries)
    {
        if (_llmRetries >= maxLlmRetries) return false;
        _llmRetries++;
        return true;
    }

    /// <summary>
    /// Backoff delay before the n-th retry. n=1 → 1s, n=2 → 3s, n=3 → 5s.
    /// Linear (2n-1) instead of exponential — avoids long stalls on a 1-shot
    /// budget while still spacing retries.
    /// </summary>
    public TimeSpan CurrentBackoff()
        => TimeSpan.FromSeconds(Math.Max(0, 2 * _llmRetries - 1));

    public GuardStats Snapshot() => new(BlockedRepeats, _llmRetries);

    /// <summary>
    /// JSON canonicalization: keys sorted ordinal, values cloned via DeepClone
    /// so the round-trip is structurally stable. Whitespace and key order in
    /// the source no longer affect the hash.
    /// </summary>
    public static string NormalizeArgsJson(JsonObject args)
    {
        var sorted = new JsonObject();
        foreach (var key in args.Select(kvp => kvp.Key).OrderBy(k => k, StringComparer.Ordinal))
            sorted[key] = args[key]?.DeepClone();
        return sorted.ToJsonString();
    }

    /// <summary>
    /// Classify HTTP-level transient errors. Prefers
    /// <see cref="HttpRequestException.StatusCode"/> when typed (502/503/504/408/429);
    /// falls back to message-keyword sniffing for cases where the SDK
    /// surfaces the failure as a generic Exception.
    /// </summary>
    public static bool IsTransientHttpError(Exception ex)
    {
        if (ex is HttpRequestException http && http.StatusCode is { } sc)
        {
            var code = (int)sc;
            if (code == 408 || code == 429 || code == 502 || code == 503 || code == 504)
                return true;
        }

        var msg = ex.Message ?? "";
        return msg.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Gateway", StringComparison.OrdinalIgnoreCase);
    }

    private static string MakeKey(ToolCall call)
        => $"{call.Tool}:{NormalizeArgsJson(call.Args)}";

    private string BuildBlockMessage(ToolCall call, int count)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Error: tool '{call.Tool}' with these exact args was already called {count} times.");
        sb.AppendLine("DO NOT repeat the same call. Try a different approach (different args, different tool, or call done).");
        if (_recent.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Recent attempts (most recent last):");
            foreach (var attempt in _recent)
                sb.AppendLine($"  • {attempt.Tool}({attempt.ArgsJson}) -> {attempt.Result}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");

    private readonly record struct RecentAttempt(string Tool, string ArgsJson, string Result);
}

/// <summary>
/// Snapshot of guard activity at run end. Surfaced via
/// <see cref="AgentLoopRun.GuardStats"/> so operators can correlate
/// false-positive rates with model/prompt changes without re-instrumenting.
/// </summary>
public sealed record GuardStats(int BlockedRepeats, int LlmRetries)
{
    public static readonly GuardStats Empty = new(0, 0);
}
