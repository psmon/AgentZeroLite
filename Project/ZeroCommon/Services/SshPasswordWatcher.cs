using Agent.Common.Data.Entities;
using Agent.Common.Module;

namespace Agent.Common.Services;

/// <summary>
/// Types a stored ssh password into the PTY when the remote host asks for it — the
/// host-agnostic port of the WPF host's <c>SshPasswordAutofill</c> (M0021 follow-up #5),
/// which lives in that app's UI layer and therefore never reached the Avalonia host.
///
/// <para>OpenSSH refuses a password on argv (process listings would leak it), so the
/// only way to answer its <c>getpass()</c> is to write at the prompt. The WPF original
/// found <see cref="ITerminalSession.OutputReceived"/> unreliable for some session
/// lifetimes and switched to polling the buffer; this keeps that proven shape, with the
/// UI-thread timer replaced by a background one so no host's dispatcher is assumed.</para>
///
/// <para>The plaintext lives in one field and is cleared on delivery, expiry or dispose.
/// It is never written to a log, a file, or the clipboard.</para>
/// </summary>
public sealed class SshPasswordWatcher : IDisposable
{
    /// <summary>Matches <c>Password:</c> and <c>user@host's password:</c> alike.</summary>
    public const string PromptNeedle = "assword:";

    private const int DefaultPollMs = 150;
    private const int DefaultSettleMs = 500;
    private static readonly TimeSpan ExpireAfter = TimeSpan.FromMinutes(2);

    private readonly ITerminalSession _session;
    private readonly Action<string> _log;
    private readonly int _settleMs;
    private readonly DateTime _expireAt;
    private readonly Timer _timer;

    private string? _plaintext;
    private int _lastScannedLen;
    private int _ticks;
    private int _inTick;
    private bool _deliveryScheduled;
    private bool _delivered;
    private bool _disposed;

    /// <summary>
    /// Arms a watcher for a definition, or returns null when this launch needs none:
    /// not remote, not password auth, nothing stored, or the stored blob will not
    /// decrypt here (another machine or account — the operator re-enters it).
    /// </summary>
    public static SshPasswordWatcher? ArmFor(
        CliDefinition definition,
        ITerminalSession session,
        Func<string?, string?> unprotect,
        Action<string>? log = null)
    {
        if (!definition.IsRemote) return null;
        if (SshCommandBuilder.ParseAuthMethod(definition.SshAuthMethod) != SshAuthMode.Password) return null;
        if (string.IsNullOrEmpty(definition.EncryptedPassword)) return null;

        string? plaintext;
        try { plaintext = unprotect(definition.EncryptedPassword); }
        catch (Exception ex)
        {
            (log ?? AppLogger.Log)($"[Ssh-AF] decrypt threw for '{definition.Name}': {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        if (string.IsNullOrEmpty(plaintext))
        {
            (log ?? AppLogger.Log)($"[Ssh-AF] '{definition.Name}' has a stored password this account cannot decrypt — type it at the prompt and save it again.");
            return null;
        }
        return new SshPasswordWatcher(session, plaintext, log);
    }

    public SshPasswordWatcher(ITerminalSession session, string plaintext, Action<string>? log = null,
        int pollMs = DefaultPollMs, int settleMs = DefaultSettleMs)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _plaintext = plaintext;
        _log = log ?? AppLogger.Log;
        _settleMs = settleMs;
        _expireAt = DateTime.UtcNow + ExpireAfter;
        _log($"[Ssh-AF] armed session=\"{session.SessionId}\" pwLen={plaintext.Length} pollMs={pollMs} settle={settleMs}ms expire={ExpireAfter.TotalSeconds}s");
        _timer = new Timer(_ => Tick(), null, pollMs, pollMs);
    }

    /// <summary>True once the password has been written to the PTY.</summary>
    public bool Delivered => _delivered;

    private void Tick()
    {
        if (_disposed || _delivered || _deliveryScheduled) return;
        // A tick that overruns the poll interval must not re-enter the scan.
        if (Interlocked.Exchange(ref _inTick, 1) != 0) return;
        try
        {
            if (DateTime.UtcNow > _expireAt)
            {
                _log($"[Ssh-AF] expired session=\"{_session.SessionId}\" ticks={_ticks} — type the password at the prompt.");
                Dispose();
                return;
            }

            _ticks++;
            var len = _session.OutputLength;
            if (len <= _lastScannedLen) return;

            // Scan only what is new, plus the previous tail, so a prompt split across
            // two reads still matches.
            var start = Math.Max(_lastScannedLen - PromptNeedle.Length, 0);
            var chunk = _session.ReadOutput(start, len - start);
            _lastScannedLen = len;
            if (string.IsNullOrEmpty(chunk)) return;

            if (chunk.Contains(PromptNeedle, StringComparison.OrdinalIgnoreCase))
                ScheduleDelivery();
        }
        catch (Exception ex)
        {
            _log($"[Ssh-AF] scan failed session=\"{_session.SessionId}\": {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _inTick, 0);
        }
    }

    /// <summary>Let ssh finish switching the tty to raw mode before writing to it.</summary>
    private void ScheduleDelivery()
    {
        if (_delivered || _deliveryScheduled || _disposed) return;
        _deliveryScheduled = true;
        _log($"[Ssh-AF] prompt seen session=\"{_session.SessionId}\" — delivering in {_settleMs}ms");
        _ = Task.Delay(_settleMs).ContinueWith(_ => Deliver(), TaskScheduler.Default);
    }

    private void Deliver()
    {
        if (_delivered || _disposed) return;
        var pw = _plaintext;
        if (string.IsNullOrEmpty(pw)) { Dispose(); return; }

        _delivered = true;
        try
        {
            // WriteAndSubmit writes the text, then Enter after a short gap, so getpass
            // reads them as two events.
            _session.WriteAndSubmit(pw);
            _log($"[Ssh-AF] delivered session=\"{_session.SessionId}\" bytes={pw.Length}");
        }
        catch (Exception ex)
        {
            _log($"[Ssh-AF] delivery failed session=\"{_session.SessionId}\": {ex.GetType().Name}: {ex.Message}");
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
        _plaintext = null;
        try { _timer.Dispose(); } catch { }
    }
}
