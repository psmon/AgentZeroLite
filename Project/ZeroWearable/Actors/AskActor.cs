using Akka.Actor;
using Akka.Event;

namespace ZeroWearable.Actors;

/// <summary>
/// The one actor the device talks to. Deliberately dumb: it answers a string with a
/// string, because that is the pair the C++ client can serialize without a .NET
/// serializer on its side (Akka's `primitive` serializer, id 17, manifest "S").
/// </summary>
public sealed class AskActor : UntypedActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private int _count;

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case string text:
                _count++;
                var reply = Answer(text, _count);
                _log.Info("ask #{0} from {1}: {2}", _count, Sender.Path, text);
                Sender.Tell(reply, Self);
                break;

            case int n:
                Sender.Tell(n * 2, Self);
                break;

            default:
                Unhandled(message);
                break;
        }
    }

    private static string Answer(string text, int n)
    {
        var t = text.Trim();

        if (t.Length == 0)
            return "(empty question)";

        if (t.Equals("ping", StringComparison.OrdinalIgnoreCase))
            return "pong";

        if (t.Equals("time", StringComparison.OrdinalIgnoreCase))
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        if (t.Equals("who", StringComparison.OrdinalIgnoreCase))
            return $"AskActor on {Environment.MachineName}, .NET {Environment.Version}";

        return $"#{n} you said \"{t}\" ({t.Length} chars)";
    }
}
