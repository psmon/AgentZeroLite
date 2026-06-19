using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Porta.Pty;
using Agent.Common;
using Agent.Common.Services;

namespace AgentZeroAvalonia.Services;

/// <summary>
/// cross-platform <see cref="ITerminalSession"/> 구현. WPF의
/// <c>ConPtyTerminalSession</c>(ConPTY 전용 EasyWindowsTerminalControl)을 대체한다.
/// Porta.Pty가 OS별 PTY를 추상화한다: Windows=ConPTY, macOS/Linux=forkpty 네이티브 shim.
///
/// 현 단계는 원시 I/O MVP — PTY 바이트를 그대로 출력 로그에 누적하고
/// <see cref="OutputReceived"/>로 푸시한다. 완전한 VT100 에뮬레이션(커서 주소지정,
/// 색상, TUI 리드로우)은 후속 렌더러 단계에서 다룬다.
/// </summary>
public sealed class PtyTerminalSession : ITerminalSession, IDisposable
{
    private readonly IPtyConnection _pty;
    private readonly string _sessionId;
    private readonly string _internalId = Guid.NewGuid().ToString("N")[..8];
    private readonly StringBuilder _outputLog = new();
    private readonly object _logLock = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>OS 기본 셸을 띄워 세션을 생성한다(비동기 — SpawnAsync).</summary>
    public static async Task<PtyTerminalSession> CreateAsync(
        string sessionId, string? shellApp = null, string? cwd = null,
        int cols = 120, int rows = 30, CancellationToken ct = default)
    {
        var options = new PtyOptions
        {
            Name = sessionId,
            Cols = cols,
            Rows = rows,
            Cwd = cwd ?? Environment.CurrentDirectory,
            App = shellApp ?? DefaultShell(),
            CommandLine = Array.Empty<string>(),
            Environment = new Dictionary<string, string>(),
        };
        var pty = await PtyProvider.SpawnAsync(options, ct);
        return new PtyTerminalSession(pty, sessionId);
    }

    private PtyTerminalSession(IPtyConnection pty, string sessionId)
    {
        _pty = pty;
        _sessionId = sessionId;
        _ = Task.Run(ReadLoopAsync);
    }

    private static string DefaultShell()
    {
        if (OperatingSystem.IsWindows()) return "powershell.exe";
        if (OperatingSystem.IsMacOS()) return "/bin/zsh";
        return "/bin/bash";
    }

    // ── 출력 읽기 루프 ────────────────────────────────────────────────
    private async Task ReadLoopAsync()
    {
        var buffer = new byte[4096];
        var chars = new char[8192];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await _pty.ReaderStream.ReadAsync(buffer.AsMemory(), _cts.Token);
                if (n <= 0) break;

                // 멀티바이트 문자가 읽기 경계에 걸칠 수 있으므로 stateful Decoder 사용.
                int charCount = _decoder.GetChars(buffer, 0, n, chars, 0);
                if (charCount == 0) continue;

                var text = new string(chars, 0, charCount);
                lock (_logLock) _outputLog.Append(text);
                try { OutputReceived?.Invoke(new TerminalOutputFrame(text, DateTimeOffset.UtcNow)); }
                catch { /* subscriber bug, not load-bearing */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLogger.Log($"[Pty] read loop ended | id={_internalId}: {ex.Message}"); }
    }

    public string SessionId => _sessionId;
    public string InternalId => _internalId;
    public bool IsRunning => !_disposed;

    public event Action<TerminalOutputFrame>? OutputReceived;
#pragma warning disable CS0067 // MVP: 헬스 상태머신 미구현이라 발생시키지 않음(인터페이스 충족용)
    public event Action<TerminalHealthState>? HealthChanged;
#pragma warning restore CS0067
    public TerminalHealthState HealthState => TerminalHealthState.Alive; // MVP: 헬스 추적 생략

    public int OutputLength { get { lock (_logLock) return _outputLog.Length; } }

    public string ReadOutput(int start, int length)
    {
        lock (_logLock)
        {
            if (length <= 0 || start < 0 || start >= _outputLog.Length) return "";
            var safe = Math.Min(length, _outputLog.Length - start);
            return safe > 0 ? _outputLog.ToString(start, safe) : "";
        }
    }

    public string GetConsoleText() { lock (_logLock) return _outputLog.ToString(); }

    public void Write(ReadOnlySpan<char> text)
    {
        if (_disposed) return;
        var bytes = Encoding.UTF8.GetBytes(text.ToArray());
        try
        {
            _pty.WriterStream.Write(bytes, 0, bytes.Length);
            _pty.WriterStream.Flush();
        }
        catch (Exception ex) { AppLogger.Log($"[Pty] write failed | id={_internalId}: {ex.Message}"); }
    }

    public async Task WriteAsync(ReadOnlyMemory<char> text, CancellationToken ct = default)
    {
        if (_disposed) return;
        var bytes = Encoding.UTF8.GetBytes(text.ToArray());
        try
        {
            await _pty.WriterStream.WriteAsync(bytes.AsMemory(), ct);
            await _pty.WriterStream.FlushAsync(ct);
        }
        catch (Exception ex) { AppLogger.Log($"[Pty] writeAsync failed | id={_internalId}: {ex.Message}"); }
    }

    public void WriteAndSubmit(string text) { Write(text.AsSpan()); Write("\r".AsSpan()); }
    public void WriteAndEnter(string text) { Write(text.AsSpan()); Write("\r".AsSpan()); }

    public void SendControl(TerminalControl control)
    {
        if (_disposed) return;
        ReadOnlySpan<char> seq = control switch
        {
            TerminalControl.Interrupt => "\x03",
            TerminalControl.Escape => "\x1b",
            TerminalControl.Enter => "\r",
            TerminalControl.Tab => "\t",
            TerminalControl.BackTab => "\x1b[Z",
            TerminalControl.Backspace => "\x7f",
            TerminalControl.Space => " ",
            TerminalControl.Delete => "\x1b[3~",
            TerminalControl.Home => "\x1b[H",
            TerminalControl.End => "\x1b[F",
            TerminalControl.PageUp => "\x1b[5~",
            TerminalControl.PageDown => "\x1b[6~",
            TerminalControl.DownArrow => "\x1b[B",
            TerminalControl.UpArrow => "\x1b[A",
            TerminalControl.LeftArrow => "\x1b[D",
            TerminalControl.RightArrow => "\x1b[C",
            TerminalControl.ClearScreen => "\x1b[2J\x1b[H",
            _ => "",
        };
        if (seq.Length == 0) return;
        Write(seq);
    }

    /// <summary>PTY 크기 조정 (UI 리사이즈 시 호출). 인터페이스 외 보너스.</summary>
    public void Resize(int cols, int rows)
    {
        if (_disposed) return;
        try { _pty.Resize(cols, rows); } catch { /* 종료 중일 수 있음 */ }
    }

    public void NoteInputAttempt(string source) { /* MVP: 헬스 상태머신 생략 */ }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _pty.Kill(); } catch { }
        try { _pty.Dispose(); } catch { }
        _cts.Dispose();
    }
}
