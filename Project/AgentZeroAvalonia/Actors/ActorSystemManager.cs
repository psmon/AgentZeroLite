// ───────────────────────────────────────────────────────────
// ActorSystemManager — the Avalonia host's access point to the Akka ActorSystem.
//
// A copy of the WPF host's class (Project/AgentZeroWpf/Actors/ActorSystemManager.cs),
// which was already free of WPF types: the HOCON `synchronized-dispatcher` captures
// whatever SynchronizationContext is current at Initialize(), and App.axaml.cs calls
// it on the Avalonia UI thread. Same actor topology (/user/stage → bot → loop,
// ws-*/term-*), same coordinated shutdown with exit-clr = on.
// ───────────────────────────────────────────────────────────

using Akka.Actor;
using Akka.Configuration;
using Agent.Common.Actors;

namespace AgentZeroAvalonia.Actors;

public static class ActorSystemManager
{
    private static ActorSystem? _system;
    private static IActorRef? _stage;

    public static ActorSystem System => _system
        ?? throw new InvalidOperationException("ActorSystem not initialized. Call Initialize() first.");

    public static IActorRef Stage => _stage
        ?? throw new InvalidOperationException("StageActor not initialized. Call Initialize() first.");

    public static bool IsInitialized => _system is not null;

    public static void Initialize()
    {
        if (_system is not null) return;

        var config = ConfigurationFactory.ParseString(@"
            akka {
                loglevel = INFO
                loggers = [""Akka.Event.DefaultLogger""]

                actor {
                    # UI-thread dispatcher: SynchronizedDispatcher posts through the
                    # SynchronizationContext current at Initialize() (Avalonia's).
                    synchronized-dispatcher {
                        type = SynchronizedDispatcher
                        throughput = 10
                    }
                }

                # Graceful termination without blocking the UI thread: phases with
                # timeouts, then Environment.Exit from the shutdown hook, so a lingering
                # PTY reader thread cannot keep the process (and the instance lock) alive.
                coordinated-shutdown {
                    default-phase-timeout = 5 s
                    terminate-actor-system = on
                    exit-clr = on
                    run-by-clr-shutdown-hook = on
                }
            }
        ");

        _system = ActorSystem.Create("AgentZero", config);
        _stage = _system.ActorOf(Props.Create<StageActor>(), "stage");
    }

    /// <summary>Fire-and-forget; exit-clr ends the process when the phases are done.</summary>
    public static void Shutdown()
    {
        if (_system is null) return;
        var system = _system;
        _system = null;
        _stage = null;
        _ = CoordinatedShutdown.Get(system).Run(CoordinatedShutdown.ClrExitReason.Instance);
    }
}
