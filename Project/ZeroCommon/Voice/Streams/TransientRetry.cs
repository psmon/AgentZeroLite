using System.Net.Http;
using Akka.Event;

namespace Agent.Common.Voice.Streams;

/// <summary>
/// Bounded retry helper for STT/TTS network calls (P3). One call attempts
/// the action; on transient failure (HTTP, IO, timeout) it backs off and
/// retries up to <c>maxAttempts</c>. <see cref="OperationCanceledException"/>
/// and <see cref="ArgumentException"/> propagate immediately — those are
/// caller-side problems, not transient.
///
/// Keeps the policy in one place so STT / TTS workers (and any future audio
/// HTTP work) share identical behaviour. We deliberately stop short of
/// <c>RestartFlow.WithBackoff</c> on the entire flow — that would tear down
/// per-element state and reopen sockets each time, which is heavier than
/// the per-call jitter we actually need.
/// </summary>
internal static class TransientRetry
{
    public static async Task<T> WithBackoffAsync<T>(
        Func<Task<T>> action,
        int maxAttempts,
        TimeSpan baseDelay,
        ILoggingAdapter? log = null)
    {
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (ArgumentException) { throw; }
            catch (Exception ex) when (IsTransient(ex))
            {
                last = ex;
                if (attempt == maxAttempts) break;
                var delay = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                log?.Warning("[TransientRetry] attempt {0}/{1} threw {2}; retrying in {3} ms",
                    attempt, maxAttempts, ex.GetType().Name, (int)delay.TotalMilliseconds);
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }
        throw last!;
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException => true,
        TaskCanceledException => true,   // HttpClient timeout
        TimeoutException => true,
        System.IO.IOException => true,
        _ => false,
    };
}
