using System.Diagnostics;
using System.IO;
using System.Text;

namespace Agent.Common;

/// <summary>
/// Simple thread-safe in-memory logger.
/// All capture strategies write here so the log panel can display diagnostics.
/// </summary>
public static class AppLogger
{
    private static readonly object Lock = new();
    private static readonly List<string> Entries = [];
    private static bool _consoleEnabled;
    private static bool _debuggerEnabled;

    // File logging
    private static StreamWriter? _fileWriter;
    private static string? _logFilePath;
    private static long _fileSizeEstimate;
    private const long MaxLogBytes = 100 * 1024; // 100KB

    public static string? LogFilePath
    {
        get
        {
            lock (Lock)
                return _logFilePath;
        }
    }

    /// <summary>Fired on the thread that called Log/LogError.</summary>
    public static event Action<string>? EntryAdded;

    public static void EnableConsoleOutput()
    {
        _consoleEnabled = true;
        foreach (var entry in GetAll())
            Console.Error.WriteLine(entry);
    }

    public static void EnableDebuggerOutput()
    {
        _debuggerEnabled = true;
        foreach (var entry in GetAll())
            Debug.WriteLine(entry);
    }

    public static void EnableFileOutput(string basePath)
    {
        lock (Lock)
        {
            var candidateBasePaths = GetCandidateLogBasePaths(basePath);
            foreach (var candidateBasePath in candidateBasePaths)
            {
                try
                {
                    var logDir = Path.Combine(candidateBasePath, "logs");
                    Directory.CreateDirectory(logDir);
                    _logFilePath = Path.Combine(logDir, "app-log.txt");

                    if (File.Exists(_logFilePath))
                    {
                        _fileSizeEstimate = new FileInfo(_logFilePath).Length;
                        if (_fileSizeEstimate > MaxLogBytes)
                            RotateLogFile();
                    }
                    else
                    {
                        _fileSizeEstimate = 0;
                    }

                    _fileWriter = new StreamWriter(_logFilePath, append: true, new UTF8Encoding(false))
                    {
                        AutoFlush = true
                    };

                    foreach (var entry in Entries)
                        _fileWriter.WriteLine(entry);

                    return;
                }
                catch
                {
                    _fileWriter?.Dispose();
                    _fileWriter = null;
                    _logFilePath = null;
                    _fileSizeEstimate = 0;
                }
            }
        }
    }

    public static void Log(string message)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (Lock)
        {
            Entries.Add(entry);
            WriteToFile(entry);
        }
        if (_consoleEnabled) Console.Error.WriteLine(entry);
        if (_debuggerEnabled) Debug.WriteLine(entry);
        EntryAdded?.Invoke(entry);
    }

    public static void LogError(string message, Exception? ex = null)
    {
        string detail = ex is null
            ? message
            : $"{message} | {ex}";
        string entry = $"[{DateTime.Now:HH:mm:ss.fff}] ❌ {detail}";
        lock (Lock)
        {
            Entries.Add(entry);
            WriteToFile(entry);
        }
        if (_consoleEnabled) Console.Error.WriteLine(entry);
        if (_debuggerEnabled) Debug.WriteLine(entry);
        EntryAdded?.Invoke(entry);
    }

    public static string[] GetAll()
    {
        lock (Lock) return [.. Entries];
    }

    public static void Clear()
    {
        lock (Lock) Entries.Clear();
    }

    private static void WriteToFile(string entry)
    {
        if (_fileWriter is null) return;
        _fileWriter.WriteLine(entry);
        _fileSizeEstimate += entry.Length + 2;
        if (_fileSizeEstimate > MaxLogBytes)
            RotateLogFile();
    }

    private static void RotateLogFile()
    {
        if (string.IsNullOrEmpty(_logFilePath))
            return;

        _fileWriter?.Dispose();
        _fileWriter = null;

        try
        {
            var lines = File.ReadAllLines(_logFilePath!);
            var keep = lines[^(lines.Length / 2)..];
            File.WriteAllLines(_logFilePath!, keep, new UTF8Encoding(false));
            _fileSizeEstimate = new FileInfo(_logFilePath!).Length;
        }
        catch
        {
            _fileSizeEstimate = 0;
        }

        _fileWriter = new StreamWriter(_logFilePath!, append: true, new UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }

    private static IEnumerable<string> GetCandidateLogBasePaths(string preferredBasePath)
    {
        if (!string.IsNullOrWhiteSpace(preferredBasePath))
            yield return preferredBasePath;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return Path.Combine(localAppData, "AgentZeroWpf");

        yield return Path.Combine(Path.GetTempPath(), "AgentZeroWpf");
    }
}
