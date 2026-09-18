using System.Runtime.Versioning;

namespace Agent.Common.Platform;

/// <summary>
/// One GUI per user session (M0033). Two processes on the same SQLite file fight over
/// locks and drift apart, which is why the WPF host has always enforced a single
/// instance with a named mutex. Named mutexes are Windows-only (the <c>Local\</c>
/// namespace does not exist on Unix), so the guard picks an implementation per OS.
///
/// <para>The Windows name is deliberately the one the WPF host uses
/// (<c>Local\AgentZeroLite.SingleInstance</c>): the WPF GUI and the Avalonia GUI share
/// one database, so they must not run side by side either.</para>
/// </summary>
public interface ISingleInstanceGuard : IDisposable
{
    /// <summary>True when this process now owns the instance; false when another already does.</summary>
    bool TryAcquire();
}

public static class SingleInstanceGuard
{
    public const string DefaultId = "AgentZeroLite";

    public static ISingleInstanceGuard Create(string id = DefaultId)
        => OperatingSystem.IsWindows()
            ? new WindowsMutexGuard(id)
            : new FileLockGuard(id);
}

/// <summary>Windows: named mutex, same semantics and name as the WPF host.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMutexGuard : ISingleInstanceGuard
{
    private readonly string _name;
    private Mutex? _mutex;

    public WindowsMutexGuard(string id) => _name = $@"Local\{id}.SingleInstance";

    public bool TryAcquire()
    {
        if (_mutex is not null) return true;
        var mutex = new Mutex(initiallyOwned: true, _name, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return false;
        }
        _mutex = mutex;
        return true;
    }

    public void Dispose()
    {
        try
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch { /* releasing on the way out */ }
        _mutex = null;
    }
}

/// <summary>
/// Unix (macOS/Linux) — and usable on Windows too: a lock file opened with
/// <see cref="FileShare.None"/>. A second process fails to open it; when the owner
/// dies the OS drops the handle and the lock with it.
/// </summary>
public sealed class FileLockGuard : ISingleInstanceGuard
{
    private readonly string _lockPath;
    private FileStream? _stream;

    public FileLockGuard(string id, string? directory = null)
    {
        var dir = directory ?? AppPaths.DataRoot;
        Directory.CreateDirectory(dir);
        _lockPath = Path.Combine(dir, $"{id}.lock");
    }

    public string LockPath => _lockPath;

    public bool TryAcquire()
    {
        if (_stream is not null) return true;
        try
        {
            _stream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;   // another instance holds it
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        _stream = null;
    }
}
