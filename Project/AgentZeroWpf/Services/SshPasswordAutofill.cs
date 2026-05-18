using System.Windows.Threading;
using Agent.Common;
using AgentZeroWpf.Module;

namespace AgentZeroWpf.Services;

/// <summary>
/// M0021 follow-up #5: when an ssh tab opens with a stored password and the
/// PTY shows the <c>Password:</c> prompt, type the plaintext + Enter into the
/// PTY after a short settle delay so ssh's getpass() reads it as if the user
/// typed it.
/// <para>
/// Earlier attempts went through <c>ITerminalSession.OutputReceived</c>
/// (event-driven). Real-world testing showed the event silently never fires
/// for some session lifetimes — output grew from 471 → 502 chars while
/// <c>frames=0</c> in the autofill log for 111 s. Root cause unverified but
/// likely a ThreadPool / subscription race inside <see cref="ConPtyTerminalSession"/>'s
/// 50 ms poll timer. Rather than chase it, this version drops the event
/// subscription entirely and uses simple WPF-thread polling: every 150 ms,
/// read the PTY buffer, scan the new portion for <c>"assword:"</c>, deliver
/// the password + Enter when found.
/// </para>
/// Plaintext lives only in this object's field, cleared on delivery, expiry,
/// or Dispose. Clipboard fallback in <c>InitializeTerminal</c> remains as a
/// safety net for unusual prompt formats.
/// </summary>
internal sealed class SshPasswordAutofill : IDisposable
{
    private const int PollMs = 150;
    private const int SettleDelayMs = 500;
    private static readonly TimeSpan ExpireAfter = TimeSpan.FromMinutes(2);
    private const string PromptNeedle = "assword:";

    private readonly ConsoleTabInfo _tab;
    private readonly DispatcherTimer _pollTimer;
    private readonly DateTime _expireAt;
    private string? _plaintext;
    private int _lastScannedLen;
    private int _tickCount;
    private bool _deliveryScheduled;
    private bool _delivered;
    private bool _disposed;

    public SshPasswordAutofill(ConsoleTabInfo tab, string plaintext)
    {
        _tab = tab;
        _plaintext = plaintext;
        _expireAt = DateTime.UtcNow + ExpireAfter;
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(PollMs),
        };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
        AppLogger.Log($"[Ssh-AF] ctor tab=\"{_tab.Title}\" pwLen={plaintext.Length} needle=\"{PromptNeedle}\" pollMs={PollMs} settle={SettleDelayMs}ms expire={ExpireAfter.TotalSeconds}s");
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (_disposed || _delivered) { Dispose(); return; }
        if (_deliveryScheduled) return;  // settle timer is running; just wait

        if (DateTime.UtcNow > _expireAt)
        {
            AppLogger.Log($"[Ssh-AF] EXPIRED tab=\"{_tab.Title}\" ticks={_tickCount} lastScannedLen={_lastScannedLen} — clipboard fallback still active.");
            Dispose();
            return;
        }

        _tickCount++;
        var session = _tab.Session;
        if (session is null)
        {
            if (_tickCount % 7 == 1)
                AppLogger.Log($"[Ssh-AF] tick#{_tickCount} tab=\"{_tab.Title}\" waiting: tab.Session is null");
            return;
        }

        try
        {
            var len = session.OutputLength;
            if (len <= _lastScannedLen) return;  // no growth

            // Scan only the NEW portion to keep allocations small, but include
            // the previous PromptNeedle-length tail so a needle that straddles
            // tick boundaries still matches.
            var scanStart = Math.Max(_lastScannedLen - PromptNeedle.Length, 0);
            var scanLen = len - scanStart;
            var chunk = session.ReadOutput(scanStart, scanLen);
            _lastScannedLen = len;

            if (string.IsNullOrEmpty(chunk)) return;
            var matched = chunk.Contains(PromptNeedle, StringComparison.OrdinalIgnoreCase);
            // Log every growth so the operator can see what the PTY actually
            // emits — invaluable when the needle doesn't match an unusual
            // prompt format.
            var snip = chunk.Length <= 160 ? chunk : chunk.Substring(chunk.Length - 160);
            AppLogger.Log($"[Ssh-AF] tick#{_tickCount} tab=\"{_tab.Title}\" outputLen={len} (+{scanLen}) match={matched} tail=<<<{Escape(snip)}>>>");
            if (matched)
                ScheduleDelivery();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Ssh-AF] poll-scan failed tab=\"{_tab.Title}\": {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Defer the actual write by 500 ms so ssh has time to fully
    /// transition into raw-tty mode before we send the password.</summary>
    private void ScheduleDelivery()
    {
        if (_delivered || _deliveryScheduled || _disposed) return;
        _deliveryScheduled = true;
        AppLogger.Log($"[Ssh-AF] delivery scheduled tab=\"{_tab.Title}\" delayMs={SettleDelayMs}");

        // We're already on the UI thread (DispatcherTimer.Tick), so creating
        // another DispatcherTimer here attaches to the application dispatcher
        // and ticks normally.
        var settleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(SettleDelayMs),
        };
        settleTimer.Tick += (_, _) =>
        {
            settleTimer.Stop();
            Deliver();
        };
        settleTimer.Start();
    }

    private void Deliver()
    {
        if (_delivered || _disposed) return;
        var session = _tab.Session;
        var pw = _plaintext;
        if (session is null || string.IsNullOrEmpty(pw))
        {
            AppLogger.Log($"[Ssh-AF] DELIVER skipped tab=\"{_tab.Title}\" sessionNull={session is null} pwEmpty={string.IsNullOrEmpty(pw)}");
            Dispose();
            return;
        }

        _delivered = true;
        try
        {
            // WriteAndSubmit: writes text, then sends \r after a 50 ms gap so
            // ssh's getpass treats text and submit as separate events.
            session.WriteAndSubmit(pw);
            AppLogger.Log($"[Ssh-AF] DELIVERED tab=\"{_tab.Title}\" pwBytes={pw.Length} sessionId={session.SessionId}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Ssh-AF] DELIVER FAILED tab=\"{_tab.Title}\": {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _pollTimer.Stop(); } catch { }
        _pollTimer.Tick -= OnPollTick;
        _plaintext = null;
    }

    /// <summary>Render control chars visible so diagnostic logs stay single-line.</summary>
    private static string Escape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                '\x1b' => "\\e",
                < ' ' => $"\\x{(int)c:X2}",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }
}
