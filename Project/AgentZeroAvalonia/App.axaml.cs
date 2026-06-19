using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Agent.Common;
using Agent.Common.Data;
using AgentZeroAvalonia.Actors;
using AgentZeroAvalonia.Views;

namespace AgentZeroAvalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 진단 로깅(exe 옆 logs/app-log.txt) + 전역 예외 포착.
            try { AppLogger.EnableFileOutput(AppContext.BaseDirectory); } catch { }
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) AppLogger.LogError("[CRASH] AppDomain", ex);
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                AppLogger.LogError("[CRASH] UnobservedTask", e.Exception); e.SetObserved();
            };
            AppLogger.Log("[App] 시작");

            // EF Core SQLite DB 생성/마이그레이션 + 기본 CliDefinition 시드.
            // (%LOCALAPPDATA%\AgentZeroLite\agentZeroLite.db — cross-platform 경로)
            try
            {
                AppDbContext.InitializeDatabase();
            }
            catch (Exception ex)
            {
                AppLogger.LogError("[DB] InitializeDatabase 실패", ex);
            }

            // Akka ActorSystem을 UI 스레드(=현재 SynchronizationContext)에서 초기화.
            // SynchronizedDispatcher가 이 시점의 Avalonia SyncContext를 캡처해
            // 액터→UI 마샬링에 사용한다. (WPF 버전과 동일한 패턴)
            ActorSystemManager.Initialize();

            desktop.MainWindow = new MainWindow();

            // 종료 시 Akka graceful shutdown (fire-and-forget; exit-clr=on이 CLR 종료 담당).
            desktop.ShutdownRequested += (_, _) => ActorSystemManager.Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
