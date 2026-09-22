using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AgentOne.Services;

namespace AgentOne.Agent;

/// <summary>
/// The other end of <see cref="SessionServer"/>'s pipe: sends one request,
/// hands every event to a callback as it arrives, answers the server's
/// questions through another, and returns the result. `agent-one ask` and the
/// self-test are both this class with different callbacks.
/// </summary>
public static class SessionClient
{
    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>How long to wait for the server to accept, before "no background session is listening".</summary>
    public static int ConnectTimeoutMs { get; set; } = 3000;

    /// <param name="onEvent">Every event before the result, in order.</param>
    /// <param name="answer">Called for "ask" and "choose"; its return goes back as the answer. Null answers "" (no / the recommendation).</param>
    /// <returns>The result event, or an error event when the session could not be reached.</returns>
    public static async Task<PipeEvent> SendAsync(
        string pipeName, PipeRequest request,
        Func<PipeEvent, Task>? onEvent, Func<PipeEvent, Task<string>>? answer,
        CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMs, ct);
        }
        catch (TimeoutException)
        {
            return new PipeEvent { Event = "error", Text = "no background session is listening — start one with `agent-one session start`" };
        }
        catch (IOException ex)
        {
            return new PipeEvent { Event = "error", Text = "could not reach the background session: " + ex.Message };
        }

        using var reader = new StreamReader(pipe, NoBom, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, NoBom, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, AgentOneWireJson.Default.PipeRequest));

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) return new PipeEvent { Event = "error", Text = "the background session closed the connection" };

            var e = JsonSerializer.Deserialize(line, AgentOneWireJson.Default.PipeEvent);
            if (e is null) continue;

            switch (e.Event)
            {
                case "result" or "error":
                    return e;

                case "ask" or "choose":
                    if (onEvent is not null) await onEvent(e);
                    var reply = answer is null ? "" : await answer(e);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeRequest { Op = "answer", Text = reply }, AgentOneWireJson.Default.PipeRequest));
                    break;

                default:
                    if (onEvent is not null) await onEvent(e);
                    break;
            }
        }
    }
}
