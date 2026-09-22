using System.Diagnostics.CodeAnalysis;
using Akka.Actor;
using Akka.Configuration;
using Akka.Event;

namespace AgentOne.Actors;

/// <summary>
/// The one <see cref="ActorSystem"/> a CLI process runs, and its HOCON. Two
/// things are set on purpose for a command-line tool: Akka's own log lines go
/// to <b>stderr</b>, because stdout is the answer (a pipe, or one JSON object
/// under --json), and coordinated shutdown does not exit the CLR — the
/// command decides the exit code, not the actor system.
/// </summary>
public static class AgentActorSystem
{
    public const string Name = "agent-one";

    private const string Hocon = """
        akka {
            loglevel = WARNING
            stdout-loglevel = OFF
            loggers = ["AgentOne.Actors.StderrLogger, agent-one"]
            log-dead-letters = off
            log-dead-letters-during-shutdown = off
            coordinated-shutdown {
                exit-clr = off
                run-by-clr-shutdown-hook = off
                terminate-actor-system = on
            }
        }
        """;

    /// <summary>A system with the CLI's settings. The caller terminates it.</summary>
    // The logger is found by its type NAME from the HOCON above; under trimming
    // and AOT that needs its metadata kept explicitly.
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(StderrLogger))]
    public static ActorSystem Create() =>
        ActorSystem.Create(Name, ConfigurationFactory.ParseString(Hocon));
}

/// <summary>Akka's log events on stderr, one line each, so stdout stays the program's output.</summary>
public sealed class StderrLogger : ReceiveActor
{
    public StderrLogger()
    {
        Receive<InitializeLogger>(_ => Sender.Tell(new LoggerInitialized()));
        Receive<LogEvent>(e => Console.Error.WriteLine($"[akka {e.LogLevel()}] {e.LogSource}: {e.Message}"));
    }
}
