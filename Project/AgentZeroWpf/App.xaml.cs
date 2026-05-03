using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;

using Agent.Common.Telemetry;
using AgentZeroWpf.Actors;
using AgentZeroWpf.UI.APP;

namespace AgentZeroWpf;

public partial class App : Application
{
    // Single-instance guard (GUI 모드 전용 — CLI/디버그는 영향 없음)
    // 같은 사용자 세션 안에서 AgentZero GUI는 1개만 실행됨을 보장.
    // 두 프로세스가 동시에 SQLite DB에 접근 시 lock 경합 → ActorSystem 상태 불일치 방지.
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // CLI mode: run console handler and exit without showing WPF window
        if (e.Args.Contains("-cli", StringComparer.OrdinalIgnoreCase))
        {
            int exitCode = CliHandler.Run(e.Args);
            Console.Out.Flush();
            Console.Error.Flush();
            NativeMethods.FreeConsole();
            Environment.Exit(exitCode);
        }

        // GUI 모드: 단일 인스턴스 가드
        _singleInstanceMutex = new Mutex(initiallyOwned: true,
            name: @"Local\AgentZeroLite.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "AgentZero가 이미 실행 중입니다. 작업관리자에서 확인해주세요.",
                "AgentZero",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Environment.Exit(0);
        }

        bool cliDebug = e.Args.Contains("--debug", StringComparer.OrdinalIgnoreCase);
        bool vsDebug = Debugger.IsAttached;

        if (cliDebug)
        {
            if (!NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS))
                NativeMethods.AllocConsole();

            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            Console.OutputEncoding = Encoding.UTF8;

            AppLogger.EnableConsoleOutput();
            AppLogger.Log("=== AgentZero WPF debug mode (CLI) ===");
        }

        if (vsDebug)
        {
            AppLogger.EnableDebuggerOutput();
            AppLogger.Log("=== AgentZero WPF debug mode (VS IDE) ===");
        }

        // Global unhandled exception handlers (crash diagnostics)
        DispatcherUnhandledException += (_, ex) =>
        {
            AppLogger.LogError("[CRASH] DispatcherUnhandled", ex.Exception);
            System.Windows.MessageBox.Show(
                $"Unhandled UI exception:{Environment.NewLine}{ex.Exception.Message}{Environment.NewLine}{Environment.NewLine}Log: {AppLogger.LogFilePath ?? "(unavailable)"}",
                "AgentZero startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ex.Handled = true; // Prevent app termination for recoverable errors
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception exception)
                AppLogger.LogError("[CRASH] AppDomain.Unhandled", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            AppLogger.LogError("[CRASH] UnobservedTask", ex.Exception);
            ex.SetObserved();
        };

        // 항상 파일 로깅 활성화 (문제 진단용)
        AppLogger.EnableFileOutput(AppContext.BaseDirectory);
        AppLogger.Log($"File log: {AppLogger.LogFilePath ?? "(unavailable)"}");

        // Akka.NET ActorSystem 초기화
        ActorSystemManager.Initialize();
        AppLogger.Log("[Akka] ActorSystem initialized");

        // Token usage telemetry collector — polls Claude Code / Codex CLI
        // JSONL transcripts every minute. Read-only on the source files;
        // safe to run alongside the producing CLIs.
        try
        {
            TokenUsageCollector.Instance.Start();
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[TokenCollector] start failed", ex);
        }

        if (cliDebug || vsDebug)
        {
            AppLogger.Log("=== Debug mode active ===");
        }

        try
        {
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[CRASH] Startup.MainWindow", ex);
            System.Windows.MessageBox.Show(
                $"Main window creation failed:{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}Log: {AppLogger.LogFilePath ?? "(unavailable)"}",
                "AgentZero startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { TokenUsageCollector.Instance.Stop(); }
        catch (Exception ex) { AppLogger.LogError("[TokenCollector] stop error", ex); }

        // CoordinatedShutdown — fire-and-forget. UI 스레드를 블로킹하지 않고,
        // Akka가 exit-clr=on 설정에 따라 단계 완료 후 Environment.Exit(0)을 호출한다.
        // 과거 버그: ShutdownAsync().GetAwaiter().GetResult()가 UI 스레드를 블로킹 →
        // synchronized-dispatcher가 UI 스레드에 post 못하고 데드락 → 프로세스가
        // 살아있는 채로 single-instance mutex를 붙잡아 재실행 차단.
        try
        {
            ActorSystemManager.Shutdown();
            AppLogger.Log("[Akka] CoordinatedShutdown triggered (exit-clr=on)");
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[Akka] Shutdown error", ex);
        }

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }
        catch { /* ignore */ }

        base.OnExit(e);
    }
}
