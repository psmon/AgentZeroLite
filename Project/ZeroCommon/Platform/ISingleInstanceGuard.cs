using System.Runtime.Versioning;

namespace Agent.Common.Platform;

/// <summary>
/// 같은 사용자 세션에서 GUI 인스턴스가 1개만 뜨도록 보장하는 가드.
/// 두 프로세스가 동시에 SQLite DB에 접근하면 lock 경합 → 상태 불일치가
/// 생기므로 WPF 버전부터 단일 인스턴스를 강제해 왔다.
///
/// cross-platform: named Mutex는 Windows 전용이라(Unix에서 런타임 예외)
/// OS별로 구현이 갈린다. <see cref="SingleInstanceGuard.Create"/> 팩토리가
/// 적절한 구현을 고른다.
/// </summary>
public interface ISingleInstanceGuard : IDisposable
{
    /// <summary>이 프로세스가 유일한 인스턴스면 true(소유권 획득), 이미 떠 있으면 false.</summary>
    bool TryAcquire();
}

/// <summary>OS별 단일 인스턴스 가드 팩토리.</summary>
public static class SingleInstanceGuard
{
    /// <param name="id">인스턴스 식별자(앱 고유). 예: "AgentZeroLite".</param>
    public static ISingleInstanceGuard Create(string id)
        => OperatingSystem.IsWindows()
            ? new WindowsMutexGuard(id)
            : new FileLockGuard(id);
}

/// <summary>Windows: named Mutex 기반. WPF 버전과 동일한 시맨틱.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsMutexGuard : ISingleInstanceGuard
{
    private readonly string _name;
    private Mutex? _mutex;

    public WindowsMutexGuard(string id) => _name = $@"Local\{id}.SingleInstance";

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, _name, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
        }
        return createdNew;
    }

    public void Dispose()
    {
        try
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch { /* ignore */ }
        _mutex = null;
    }
}

/// <summary>
/// Unix(macOS/Linux): 잠금 파일 + 배타적 FileShare로 단일 인스턴스 보장.
/// 파일을 FileShare.None으로 열어두면 두 번째 프로세스는 열기에 실패한다.
/// 프로세스가 죽으면 OS가 핸들을 회수 → 잠금 자동 해제.
/// </summary>
internal sealed class FileLockGuard : ISingleInstanceGuard
{
    private readonly string _lockPath;
    private FileStream? _stream;

    public FileLockGuard(string id)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentZeroLite");
        Directory.CreateDirectory(dir);
        _lockPath = Path.Combine(dir, $"{id}.lock");
    }

    public bool TryAcquire()
    {
        try
        {
            _stream = new FileStream(
                _lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            // 다른 인스턴스가 이미 잠금 보유.
            return false;
        }
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { /* ignore */ }
        _stream = null;
    }
}
