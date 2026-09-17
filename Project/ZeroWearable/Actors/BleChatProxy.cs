using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using ZeroWearable.Ble;

namespace ZeroWearable.Actors;

/// <summary>
/// Stands in for the Chat app, which speaks the older BLE line protocol rather than Akka.
///
/// The AskBot app is a real remoting peer: it has an address and <see cref="ChatActor"/>
/// answers its <c>Sender</c>. The Chat app has no ActorSystem - it writes <c>R {json}</c>
/// lines over NUS. This actor closes that gap: it *is* the Chat app as far as the rest of
/// the host is concerned, so both apps are served by the same ChatActor, the same chat CLI
/// and the same SuperTonic voice, and the firmware needs no changes at all.
///
///   device  R {json}  --> BleChatProxy --> ChatActor        (sender = this actor)
///   device  A {json}  <-- BleChatProxy <-- ChatActor        (same messages, retagged)
///   device  0xA6      <-- BleChatProxy <-- ChatActor        (speech frames, unchanged)
///
/// Translation is only tagging: the field names the firmware reads (st, id, text, seq, n,
/// done, fmt, rate, frames, ms) are the ones ChatActor already emits.
/// </summary>
public sealed class BleChatProxy : UntypedActor
{
    private readonly BleLink _link;
    private readonly IActorRef _chat;
    private readonly ILoggingAdapter _log = Context.GetLogger();

    /// <summary>Sent by the link when the device connects, so the host greets it first.</summary>
    public sealed record Greet;
    /// <summary>Sent by the link when the device goes away; its state must not outlive it.</summary>
    public sealed record Reset;
    /// <summary>One inbound line from the device, tag included.</summary>
    public sealed record Line(string Text);
    /// <summary>One inbound binary frame from the device (0xA5 microphone audio).</summary>
    public sealed record MicFrame(byte[] Data);
    /// <summary>Ask the Chat app to record for a while ("C" remote-control command).</summary>
    public sealed record Talk(int Milliseconds);
    /// <summary>Any other "C" command, e.g. {"cmd":"mode","voice":true} or {"cmd":"text","text":"…"}.</summary>
    public sealed record Command(string Json);

    public BleChatProxy(BleLink link, IActorRef chat)
    {
        _link = link;
        _chat = chat;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case Greet:
                // The firmware answers an "H" line with its own hello, which is what makes it
                // start a conversation. ChatActor produces the payload; we only retag it.
                _chat.Tell("{\"t\":\"hello\",\"name\":\"chat-app\",\"fw\":\"ble\"}", Self);
                break;

            case Reset:
                // Told from this actor so ChatActor sees the same sender - and therefore the
                // same device key - that the chat app's own lines arrive under.
                _chat.Tell(new ChatActor.DeviceGone(), Self);
                break;

            case Line line:
                Inbound(line.Text);
                break;

            case Talk talk:
                _log.Info("asking the chat app to record for {0} ms", talk.Milliseconds);
                _ = _link.SendLineAsync($"C {{\"cmd\":\"talk\",\"ms\":{talk.Milliseconds}}}");
                break;

            case Command command:
                _log.Info("sending C {0}", command.Json);
                _ = _link.SendLineAsync($"C {command.Json}");
                break;

            case MicFrame mic:
                // Forwarded as a plain byte[] so ChatActor handles the Chat app's audio with
                // exactly the code path it uses for AskBot's.
                _chat.Tell(mic.Data, Self);
                break;

            // Everything below arrives from ChatActor, addressed to this proxy.
            case string json:
                Outbound(json);
                break;

            case byte[] frame:
                // Already a 0xA6 speech frame: the Chat firmware's decoder takes it as is.
                _ = _link.SendRawAsync(frame);
                break;

            default:
                Unhandled(message);
                break;
        }
    }

    private void Inbound(string line)
    {
        if (line.Length < 2)
        {
            return;
        }
        var tag = line[0];
        var body = line[2..];

        switch (tag)
        {
            case 'R':
                // The greeting is one round trip in one direction: the host sends H, the device
                // answers with its own hello, and that answer must NOT be forwarded. ChatActor
                // would reply with another hostinfo, the device would answer again, and the two
                // ping-pong forever - measured at 175 ms per lap before this check existed.
                if (IsHello(body)) {
                    _log.Info("chat app acknowledged the greeting");
                    break;
                }
                // Everything else is a real request; the field names already match ChatActor's.
                _chat.Tell(body, Self);
                break;

            case 'S':
            case 'E':
                // HUD status / event lines: not this host's business.
                break;

            default:
                _log.Debug("ignoring '{0}' line from the chat app", tag);
                break;
        }
    }

    private static bool IsHello(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("t", out var t) &&
                   t.ValueKind == JsonValueKind.String && t.GetString() == "hello";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void Outbound(string json)
    {
        // ChatActor speaks one shape; the firmware expects a tag and no "t" field.
        string? type = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String)
                type = t.GetString();
        }
        catch (JsonException)
        {
            _log.Warning("unparseable message for the chat app: {0}", json);
            return;
        }

        var tag = type switch
        {
            "hostinfo" => 'H',
            "answer" => 'A',
            _ => '\0',
        };
        if (tag == '\0')
        {
            _log.Debug("no line tag for '{0}', dropped", type);
            return;
        }

        _ = _link.SendLineAsync($"{tag} {Strip(json)}");
    }

    /// <summary>Removes the leading "t" field; the tag carries that information on the wire.</summary>
    private static string Strip(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(json.Length);
        using (var writer = new Utf8JsonWriter(buffer,
                   new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.NameEquals("t")) continue;
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
