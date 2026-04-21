// ───────────────────────────────────────────────────────────
// ActorSystemManager — WPF 앱에서 Akka ActorSystem 접근점
//
// WPF는 DI 컨테이너 없이 코드비하인드로 동작하므로,
// 정적 싱글톤으로 ActorSystem과 주요 액터 참조를 제공합니다.
//
// 사용:
//   App.xaml.cs OnStartup  → ActorSystemManager.Initialize()
//   App.xaml.cs OnExit     → ActorSystemManager.Shutdown()
//   MainWindow / AgentBot  → ActorSystemManager.Stage.Tell(...)
// ───────────────────────────────────────────────────────────

using Akka.Actor;
using Akka.Configuration;
using Agent.Common.Actors;

namespace AgentZeroWpf.Actors;

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
    /// ActorSystem + StageActor 초기화.
    /// App.xaml.cs OnStartup에서 1회 호출.
    /// </summary>
    public static void Initialize()
    {
        if (_system is not null) return;

        var config = ConfigurationFactory.ParseString(@"
            akka {
                loglevel = INFO
                loggers = [""Akka.Event.DefaultLogger""]

                actor {
                    # WPF SynchronizationContext 디스패처 (UI 스레드 접근용)
                    synchronized-dispatcher {
                        type = SynchronizedDispatcher
                        throughput = 10
                    }
                }

                # CoordinatedShutdown — Akka 공식 graceful termination.
                # WPF에서 ActorSystem.Terminate()를 UI 스레드에서 블로킹 대기하면
                # synchronized-dispatcher 데드락 발생. CoordinatedShutdown이 단계별
                # 타임아웃을 적용하고 마지막에 Environment.Exit(0)을 호출하여
                # 잔존 백그라운드 스레드(ConPTY ReadOutputLoop 등)와 무관하게
                # 프로세스를 확실히 종료한다 → single-instance mutex도 OS가 해제.
                coordinated-shutdown {
                    default-phase-timeout = 5 s
                    terminate-actor-system = on
                    exit-clr = on
                    run-by-clr-shutdown-hook = on
                }
            }
        ");

        _system = ActorSystem.Create("AgentZero", config);

        _stage = _system.ActorOf(
            Props.Create<StageActor>(),
            "stage");
    }

    /// <summary>
    /// ActorSystem 종료.
    /// App.xaml.cs OnExit에서 호출.
    /// </summary>
    public static async Task ShutdownAsync()
    {
        if (_system is null) return;

        // CoordinatedShutdown — 단계별 타임아웃 + exit-clr=on으로 Environment.Exit까지 자동.
        // ClrExitReason: "CLR 종료 훅에서 호출됨" 의미 — Akka가 이 reason을 보고 최종 단계에서 CLR 종료.
        var system = _system;
        _system = null;
        _stage = null;
        await CoordinatedShutdown.Get(system).Run(CoordinatedShutdown.ClrExitReason.Instance);
    }

    /// <summary>
    /// Fire-and-forget shutdown — UI 스레드를 블로킹하지 않는다.
    /// Akka CoordinatedShutdown이 단계별 타임아웃 후 exit-clr=on 설정에 따라
    /// 자체적으로 Environment.Exit(0)을 호출하여 프로세스를 종료한다.
    /// </summary>
    public static void Shutdown()
    {
        if (_system is null) return;
        var system = _system;
        _system = null;
        _stage = null;
        // 백그라운드에서 실행 — 반환 Task는 exit-clr에 의해 Environment.Exit로 끊어짐.
        _ = CoordinatedShutdown.Get(system).Run(CoordinatedShutdown.ClrExitReason.Instance);
    }
}
