// ───────────────────────────────────────────────────────────
// ActorSystemManager — Avalonia 앱에서 Akka ActorSystem 접근점.
//
// WPF 버전(Project/AgentZeroWpf/Actors/ActorSystemManager.cs)을 거의
// 그대로 이식. WPF 의존성은 없었고, SynchronizedDispatcher가 캡처하는
// SynchronizationContext만 호스트가 다르다(Avalonia UI 스레드).
//
// 사용:
//   App.OnFrameworkInitializationCompleted → Initialize()
//   App.ShutdownRequested                  → Shutdown()
//   View / ViewModel                        → ActorSystemManager.Stage.Tell(...)
// ───────────────────────────────────────────────────────────

using Akka.Actor;
using Akka.Configuration;
using Agent.Common.Actors;

namespace AgentZeroAvalonia.Actors;

public static class ActorSystemManager
{
    private static ActorSystem? _system;
    private static IActorRef? _stage;

    /// <summary>Akka ActorSystem 인스턴스</summary>
    public static ActorSystem System => _system
        ?? throw new InvalidOperationException("ActorSystem not initialized. Call Initialize() first.");

    /// <summary>StageActor 참조 (최상위 액터)</summary>
    public static IActorRef Stage => _stage
        ?? throw new InvalidOperationException("StageActor not initialized. Call Initialize() first.");

    /// <summary>초기화 여부</summary>
    public static bool IsInitialized => _system is not null;

    /// <summary>
    /// ActorSystem + StageActor 초기화. 앱 시작 시 1회 호출(UI 스레드).
    /// </summary>
    public static void Initialize()
    {
        if (_system is not null) return;

        var config = ConfigurationFactory.ParseString(@"
            akka {
                loglevel = INFO
                loggers = [""Akka.Event.DefaultLogger""]

                actor {
                    # UI 스레드(현재 SynchronizationContext) 디스패처.
                    # Avalonia 메인 스레드에서 Initialize()가 호출되면 이 디스패처가
                    # Avalonia SyncContext를 캡처해 액터→UI 마샬링에 사용한다.
                    synchronized-dispatcher {
                        type = SynchronizedDispatcher
                        throughput = 10
                    }
                }

                # CoordinatedShutdown — graceful termination.
                # exit-clr=on으로 마지막 단계에서 CLR을 종료하여 잔존
                # 백그라운드 스레드와 무관하게 프로세스를 확실히 끝낸다.
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

    /// <summary>
    /// Fire-and-forget shutdown — UI 스레드를 블로킹하지 않는다.
    /// CoordinatedShutdown이 단계별 타임아웃 후 exit-clr=on에 따라
    /// Environment.Exit를 호출해 프로세스를 종료한다.
    /// </summary>
    public static void Shutdown()
    {
        if (_system is null) return;
        var system = _system;
        _system = null;
        _stage = null;
        _ = CoordinatedShutdown.Get(system).Run(CoordinatedShutdown.ClrExitReason.Instance);
    }
}
