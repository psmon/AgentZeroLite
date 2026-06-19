using System;
using Avalonia;
using Agent.Common.Platform;

namespace AgentZeroAvalonia;

// ───────────────────────────────────────────────────────────
// 진입점. WPF 버전과 동일하게 단일 실행파일이 두 모드를 가진다:
//   • 인자에 "-cli" 포함 → CLI 모드 (헤드리스, Avalonia 미기동)
//   • 그 외             → GUI 모드 (Avalonia 데스크톱 앱)
//
// 현 단계(skeleton)에서는 CLI는 스텁. 실제 CLI↔GUI IPC는 cross-platform
// 추상화(ICliIpcBridge)로 Phase 2 이후 구현한다.
// ───────────────────────────────────────────────────────────
internal static class Program
{
    // 단일 인스턴스 가드 — 프로세스 수명 동안 살아있어야 잠금이 유지된다.
    private static ISingleInstanceGuard? _instanceGuard;

    // Avalonia는 메인 스레드에서 초기화돼야 하므로 STAThread 유지(Windows 한정 의미).
    [STAThread]
    public static int Main(string[] args)
    {
        if (Array.IndexOf(args, "-cli") >= 0)
            return CliStub.Run(args);

        // GUI 모드: 단일 인스턴스 보장 (cross-platform). 두 프로세스가
        // 동시에 SQLite DB에 접근하면 lock 경합이 생기므로 막는다.
        _instanceGuard = SingleInstanceGuard.Create("AgentZeroLite");
        if (!_instanceGuard.TryAcquire())
        {
            Console.Error.WriteLine("AgentZero가 이미 실행 중입니다.");
            return 1;
        }

        try
        {
            return BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _instanceGuard.Dispose();
        }
    }

    // Avalonia 디자이너/프리뷰어가 호출하는 표준 팩토리.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
