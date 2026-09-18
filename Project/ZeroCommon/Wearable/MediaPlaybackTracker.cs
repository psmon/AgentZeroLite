using System.Diagnostics;

namespace Agent.Common.Wearable;

/// <summary>
/// Remembers what <c>open_file</c> started so <c>stop_media</c> can end it (M0032 follow-up #1).
/// ShellExecute on a song hands it to whatever player is associated, so "stop" is not a
/// call we can make — it is a process we have to find. Three ways, in order:
/// the <see cref="Process"/> ShellExecute returned (a new player instance); a known
/// player that started right after our launch (app activation returns no handle); and,
/// last, a media-stop key the host sends system-wide. Documents and images are not
/// tracked — "stop" means playback.
/// </summary>
public sealed class MediaPlaybackTracker
{
    /// <summary>Process names (without .exe) of players that ShellExecute may activate without returning a handle.</summary>
    private static readonly string[] KnownPlayers =
    {
        "Microsoft.Media.Player", "wmplayer", "Music.UI", "Video.UI", "vlc", "mpv",
        "mpc-hc64", "mpc-hc", "PotPlayerMini64", "PotPlayerMini", "foobar2000", "AIMP",
        "GOM", "GOM64", "KMPlayer", "MusicBee", "Winamp",
    };

    private readonly object _lock = new();
    private Process? _launched;
    private DateTime _launchedAt = DateTime.MinValue;
    private string? _lastPath;

    /// <summary>The alias path last handed to a player, or null when nothing is tracked.</summary>
    public string? LastPath
    {
        get { lock (_lock) return _lastPath; }
    }

    /// <summary>Call right after ShellExecute; <paramref name="process"/> may be null (existing instance took the file).</summary>
    public void Record(Process? process, string aliasPath)
    {
        lock (_lock)
        {
            _launched?.Dispose();
            _launched = process;
            _launchedAt = DateTime.Now;
            _lastPath = aliasPath;
        }
    }

    /// <summary>
    /// Stop whatever <see cref="Record"/> saw. <paramref name="fallback"/> is the host's
    /// system-wide media-stop key (user32 lives outside ZeroCommon). Returns whether
    /// something was done and, in words, what.
    /// </summary>
    public (bool Stopped, string How) Stop(Func<bool>? fallback)
    {
        Process? launched;
        DateTime launchedAt;
        string? path;
        lock (_lock)
        {
            launched = _launched;
            launchedAt = _launchedAt;
            path = _lastPath;
            _launched = null;
            _lastPath = null;
        }

        if (path is null)
            return (false, "nothing is playing that this host started");

        if (launched is not null)
        {
            var closed = TryClose(launched);
            launched.Dispose();
            if (closed) return (true, "closed the player");
        }

        // App activation: the player was already running or was started by the shell
        // without a handle for us. A known player that appeared after our launch is ours.
        foreach (var name in KnownPlayers)
        {
            Process[] candidates;
            try { candidates = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var candidate in candidates)
            {
                try
                {
                    if (candidate.StartTime >= launchedAt.AddSeconds(-2) && TryClose(candidate))
                        return (true, $"closed {name}");
                }
                catch { /* access denied / exited — not ours to close */ }
                finally { candidate.Dispose(); }
            }
        }

        if (fallback?.Invoke() == true)
            return (true, "sent the media stop key to the player");

        return (false, "could not find the player to stop");
    }

    private static bool TryClose(Process process)
    {
        try
        {
            if (process.HasExited) return false;
            if (process.CloseMainWindow() && process.WaitForExit(1500)) return true;
            if (process.HasExited) return true;
            process.Kill(entireProcessTree: true);
            return process.WaitForExit(1500);
        }
        catch
        {
            return false;
        }
    }
}
