using System;
using System.Linq;
using System.Threading;
using Akka.Actor;
using AgentZeroAvalonia.Services;
using AgentZeroAvalonia.Tools;
using Agent.Common.Platform;

namespace AgentZeroAvalonia;

// ───────────────────────────────────────────────────────────
// CLI 모드 스텁. WPF 버전의 CliHandler는 WM_COPYDATA + MemoryMappedFile로
// 동작 중인 GUI와 통신하지만, 이는 Win32 전용이다. cross-platform IPC
// (Unix domain socket / named pipe 추상화)는 Phase 2 이후 ICliIpcBridge로
// 구현한다. 지금은 사용법/자체 진단만 제공한다.
// ───────────────────────────────────────────────────────────
internal static class CliStub
{
    public static int Run(string[] args)
    {
        // PTY 백엔드 자체 진단 — GUI 없이 cross-platform PTY가 셸을 띄우고
        // I/O를 파이프하는지 검증한다. (헤드리스 검증용)
        if (args.Contains("pty-selftest"))
            return PtySelfTest();

        if (args.Contains("toolbelt-selftest"))
            return ToolbeltSelfTest();

        if (args.Contains("ipc-selftest"))
            return IpcSelfTest();

        if (args.Contains("actor-term-selftest"))
            return ActorTerminalSelfTest();

        Console.WriteLine("AgentZeroLite (Avalonia) — CLI 모드(전체 명령 라우팅)는 후속 단계입니다.");
        Console.WriteLine("진단: -cli pty-selftest | -cli toolbelt-selftest | -cli ipc-selftest | -cli actor-term-selftest");
        return 0;
    }

    private static int PtySelfTest()
    {
        try
        {
            Console.WriteLine("[pty-selftest] 셸 spawn 중…");
            using var session = PtyTerminalSession
                .CreateAsync("selftest").GetAwaiter().GetResult();

            const string marker = "PTY_SELFTEST_OK";
            var seen = new ManualResetEventSlim(false);
            session.OutputReceived += frame =>
            {
                Console.Write(frame.Text);
                if (session.GetConsoleText().Contains(marker))
                    seen.Set();
            };

            Thread.Sleep(800);                       // 셸 프롬프트 정착 대기
            session.Write($"echo {marker}".AsSpan());
            session.SendControl(Agent.Common.Services.TerminalControl.Enter);

            var ok = seen.Wait(TimeSpan.FromSeconds(8));
            Console.WriteLine();
            Console.WriteLine(ok
                ? "[pty-selftest] 통과 ✓ — 셸 출력에서 마커 확인"
                : "[pty-selftest] 실패 ✗ — 8초 내 마커 미확인");
            return ok ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[pty-selftest] 예외: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
    }

    // 에이전트 toolbelt 자체 진단 — 레지스트리에 등록된 PTY 세션을
    // list/send/read로 구동해 셸 명령 결과를 회수하는지 검증한다.
    private static int ToolbeltSelfTest()
    {
        try
        {
            Console.WriteLine("[toolbelt-selftest] 셸 spawn + 등록 중…");
            using var session = PtyTerminalSession
                .CreateAsync("tb-selftest").GetAwaiter().GetResult();
            TerminalRegistry.Register(0, 0, "shell", session);

            var belt = new PtyTerminalToolbelt();
            var list = belt.ListTerminalsAsync(CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"[toolbelt-selftest] list_terminals → {list}");

            Thread.Sleep(800); // 프롬프트 정착 대기
            const string marker = "TOOLBELT_OK";
            var sent = belt.SendToTerminalAsync(0, 0, $"echo {marker}", CancellationToken.None)
                .GetAwaiter().GetResult();
            Console.WriteLine($"[toolbelt-selftest] send_to_terminal → {sent}");

            // 출력 누적 대기 후 read.
            bool found = false;
            for (int i = 0; i < 16 && !found; i++)
            {
                Thread.Sleep(300);
                var read = belt.ReadTerminalAsync(0, 0, 4000, CancellationToken.None)
                    .GetAwaiter().GetResult();
                found = read.Contains(marker);
            }

            Console.WriteLine(found
                ? "[toolbelt-selftest] 통과 ✓ — read_terminal에서 마커 확인"
                : "[toolbelt-selftest] 실패 ✗ — 마커 미확인");
            return found ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[toolbelt-selftest] 예외: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
    }

    // cross-platform IPC 자체 진단 — 인프로세스 서버를 띄우고 클라가
    // 요청/응답 라운드트립을 하는지 검증한다 (NamedPipe / Unix domain socket).
    private static int IpcSelfTest()
    {
        try
        {
            var bridge = CliIpcBridge.Create("AgentZeroLite.selftest");
            using var server = bridge.StartServer(req => $"pong:{req}");
            Thread.Sleep(300); // 서버 수신 대기 정착

            var resp = bridge.SendRequest("ping", 3000);
            Console.WriteLine($"[ipc-selftest] 요청='ping' 응답='{resp}'");

            var ok = resp == "pong:ping";
            Console.WriteLine(ok ? "[ipc-selftest] 통과 ✓" : "[ipc-selftest] 실패 ✗");
            return ok ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ipc-selftest] 예외: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
    }

    // 액터 토폴로지 자체 진단 — 워크스페이스/터미널 액터를 만들고 PTY 세션을
    // BindSessionInWorkspace로 바인드한 뒤, 터미널 액터에 상태를 질의해
    // 세션이 정상 연결됐는지 검증한다.
    private static int ActorTerminalSelfTest()
    {
        try
        {
            Actors.ActorSystemManager.Initialize();
            var system = Actors.ActorSystemManager.System;
            var stage = Actors.ActorSystemManager.Stage;
            const string ws = "default";
            const string termId = "term-actor";

            stage.Tell(new Agent.Common.Actors.RegisterWorkspace(ws, Environment.CurrentDirectory));
            using var session = PtyTerminalSession.CreateAsync(termId).GetAwaiter().GetResult();
            stage.Tell(new Agent.Common.Actors.CreateTerminalInWorkspace(ws, termId, session.SessionId));
            stage.Tell(new Agent.Common.Actors.BindSessionInWorkspace(ws, termId, session));

            Thread.Sleep(600); // 액터 생성/바인드 정착

            var path = $"/user/stage/ws-{Agent.Common.Actors.ActorNameSanitizer.Safe(ws)}/" +
                       $"term-{Agent.Common.Actors.ActorNameSanitizer.Safe(termId)}";
            Console.WriteLine($"[actor-term-selftest] 터미널 액터 경로: {path}");

            var term = system.ActorSelection(path)
                .ResolveOne(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            var status = term.Ask<Agent.Common.Actors.TerminalStatusResponse>(
                new Agent.Common.Actors.QueryTerminalStatus(), TimeSpan.FromSeconds(2))
                .GetAwaiter().GetResult();

            Console.WriteLine($"[actor-term-selftest] status: id={status.TerminalId} running={status.IsRunning}");
            var ok = status.IsRunning;
            Console.WriteLine(ok
                ? "[actor-term-selftest] 통과 ✓ — 세션이 터미널 액터에 바인드됨"
                : "[actor-term-selftest] 실패 ✗ — 세션 미연결");
            return ok ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[actor-term-selftest] 예외: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
    }
}
