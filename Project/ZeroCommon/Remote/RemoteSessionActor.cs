// ───────────────────────────────────────────────────────────
// RemoteSessionActor — one web session ↔ one terminal, the intermediary
//
// 역할:
//   1. 바인딩된 ITerminalSession 의 출력을 웹으로 중계 (OutputReceived → sendToWeb)
//   2. 연결 직후 현재 화면 스냅샷 1회 전송 (이어가기)
//   3. 웹 입력(RemoteInputText/RemoteControlKey)을 터미널에 주입
//
// 경로: /user/stage/remote/session-{key}
//
// OutputReceived 는 세션의 50ms 타이머 스레드에서 발화하므로, 콜백에서 곧장
// sendToWeb 을 호출하지 않고 Self.Tell 로 액터 메일박스에 마샬링한다.
// ───────────────────────────────────────────────────────────

using Akka.Actor;
using Akka.Event;
using Agent.Common;
using Agent.Common.Actors;
using Agent.Common.Services;

namespace Agent.Common.Remote;

public sealed class RemoteSessionActor : ReceiveActor
{
    /// <summary>Cap on the initial "current screen" snapshot so a huge scrollback doesn't
    /// blow up the first frame. xterm keeps its own scrollback from there.</summary>
    private const int SnapshotMaxChars = 256 * 1024;

    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly string _sessionKey;
    private readonly ITerminalSession _session;
    private readonly Action<string> _sendToWeb;

    public RemoteSessionActor(string sessionKey, ITerminalSession session, Action<string> sendToWeb)
    {
        _sessionKey = sessionKey;
        _session = session;
        _sendToWeb = sendToWeb;

        Receive<Ping>(_ => Sender.Tell(new Pong("RemoteSession", Self.Path.ToString(),
            $"Key={_sessionKey}, Running={_session.IsRunning}")));

        Receive<RemoteInputText>(m =>
        {
            if (string.IsNullOrEmpty(m.Text)) return;
            _session.Write(m.Text.AsSpan());
            _session.NoteInputAttempt($"remote:write bytes={m.Text.Length}");
        });

        Receive<RemoteControlKey>(m =>
        {
            var (ctrl, raw) = MapKey(m.Key);
            if (ctrl is { } c)
            {
                _session.SendControl(c);
                _session.NoteInputAttempt($"remote:control={c}");
            }
            else if (raw is { } r)
            {
                _session.Write(r.AsSpan());
                _session.NoteInputAttempt($"remote:key={m.Key}");
            }
        });
    }

    protected override void PreStart()
    {
        // Seed the web view with the current screen (raw ANSI) so it "continues from
        // where the GUI tab is right now". Send the snapshot BEFORE subscribing so no
        // live output frame can slip ahead of it.
        try
        {
            int len = _session.OutputLength;
            int start = len > SnapshotMaxChars ? len - SnapshotMaxChars : 0;
            string snapshot = start < len ? _session.ReadOutput(start, len - start) : "";
            SafeSend(RemoteProtocol.Snapshot(snapshot));
        }
        catch (Exception ex)
        {
            _log.Warning("Remote snapshot failed for {0}: {1}", _sessionKey, ex.Message);
        }

        _session.OutputReceived += OnSessionOutput;
        AppLogger.Log($"[RemoteSession] {_sessionKey} subscribed OutputReceived | session={_session.SessionId} id={_session.InternalId} outLen={_session.OutputLength} running={_session.IsRunning}");
        _log.Info("RemoteSessionActor started: {0}", _sessionKey);
        base.PreStart();
    }

    protected override void PostStop()
    {
        _session.OutputReceived -= OnSessionOutput;
        _log.Info("RemoteSessionActor stopped: {0}", _sessionKey);
        base.PostStop();
    }

    private void OnSessionOutput(TerminalOutputFrame frame)
    {
        // Fires on the PTY timer thread. _sendToWeb only enqueues onto a thread-safe
        // channel, and OutputReceived is single-threaded per session, so calling it
        // directly is safe and keeps frames strictly after the PreStart snapshot.
        SafeSend(RemoteProtocol.Output(frame.Text));
    }

    private void SafeSend(string payload)
    {
        try { _sendToWeb(payload); }
        catch (Exception ex) { _log.Warning("Remote sendToWeb failed for {0}: {1}", _sessionKey, ex.Message); }
    }

    /// <summary>Map a named key to either a <see cref="TerminalControl"/> or a raw byte
    /// sequence (for keys the enum doesn't cover, e.g. Ctrl+D). Returns (null, null) if unknown.</summary>
    private static (TerminalControl? Control, string? Raw) MapKey(string? key) => (key ?? "").ToLowerInvariant() switch
    {
        "ctrlc" or "interrupt"      => (TerminalControl.Interrupt, null),
        "esc" or "escape"           => (TerminalControl.Escape, null),
        "cr" or "enter" or "return" => (TerminalControl.Enter, null),
        "tab"                       => (TerminalControl.Tab, null),
        "backtab" or "shifttab"     => (TerminalControl.BackTab, null),
        "backspace" or "bs"         => (TerminalControl.Backspace, null),
        "space"                     => (TerminalControl.Space, null),
        "del" or "delete"           => (TerminalControl.Delete, null),
        "home"                      => (TerminalControl.Home, null),
        "end"                       => (TerminalControl.End, null),
        "pageup" or "pgup"          => (TerminalControl.PageUp, null),
        "pagedown" or "pgdn"        => (TerminalControl.PageDown, null),
        "up"                        => (TerminalControl.UpArrow, null),
        "down"                      => (TerminalControl.DownArrow, null),
        "left"                      => (TerminalControl.LeftArrow, null),
        "right"                     => (TerminalControl.RightArrow, null),
        "clear"                     => (TerminalControl.ClearScreen, null),
        "ctrld"                     => (null, ""),
        _                           => (null, null),
    };
}
