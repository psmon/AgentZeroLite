using System.Diagnostics;
using System.IO;
using System.Text;

using Agent.Common;
using Agent.Common.Wearable;

namespace AgentZeroWpf.Services.Wearable;

/// <summary>
/// The GUI's handle on the wearable host — <c>AgentZeroWearable.exe</c>, the second process
/// that owns the watch's BLE link (see <c>Project/ZeroWearable</c> for why it is second: the
/// BLE central is WinRT and AgentZeroLite.exe's target framework must not move).
///
/// This is the same shape as <see cref="Remote.RemoteServerHost"/> — Start / Stop /
/// <see cref="IsRunning"/> / <see cref="StatusChanged"/> — except the thing being started is
/// a process rather than a listener. Nothing is configured here: the host reads
/// <c>wearable-settings.json</c>, <c>voice-settings.json</c> and <c>llm-settings.json</c>
/// itself, so "restart after a settings change" is the whole protocol between them.
///
/// <para>Output is forwarded verbatim to <see cref="LineReceived"/>. The host writes
/// <c>[category/level] message</c> lines, and the one that matters is
/// <c>[host/ready]</c> — until that arrives the process is up but the radio is not.</para>
/// </summary>
public sealed class WearableHostProcess
{
    /// <summary>Folder the host is installed into, next to AgentZeroLite.exe.</summary>
    private const string ShippedSubdir = "wearable";
    private const string HostExeName = "AgentZeroWearable.exe";

    /// <summary>The build output layout, so F5 from the repo works without an install step.</summary>
    private const string DevRelativeDir = @"..\..\..\..\ZeroWearable\bin\{0}\net10.0-windows10.0.19041.0";

    private readonly object _lock = new();
    private Process? _process;

    /// <summary>
    /// Ties the child's lifetime to ours at the kernel level, so a crash or a forced kill of
    /// AgentZeroLite cannot leave a host behind holding the BLE link and :8765. Created once;
    /// the handle is released by the OS when this process ends, which is what triggers it.
    /// </summary>
    private readonly WearableHostJob _job = new();

    /// <summary>Raised (on a background thread) when running state, readiness or last-error changes.</summary>
    public event Action? StatusChanged;

    /// <summary>Raised per stdout/stderr line, on a background thread.</summary>
    public event Action<string>? LineReceived;

    public bool IsRunning { get; private set; }

    /// <summary>True once the host printed <c>[host/ready]</c> — the radio is up.</summary>
    public bool IsReady { get; private set; }

    public int? ProcessId { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Resolved path of the exe this would start, or null when it is not built/installed.</summary>
    public static string? ResolveHostPath()
    {
        var baseDir = AppContext.BaseDirectory;

        var shipped = Path.Combine(baseDir, ShippedSubdir, HostExeName);
        if (File.Exists(shipped)) return shipped;

        // Dev layout: bin\<cfg>\net10.0-windows\ → ..\..\..\ZeroWearable\bin\<cfg>\…
        // Try this build's configuration first, then the other one, so a Debug GUI can still
        // drive a host that was only built in Release.
        foreach (var configuration in new[] { CurrentConfiguration, "Debug", "Release" })
        {
            var dev = Path.GetFullPath(Path.Combine(baseDir,
                string.Format(DevRelativeDir, configuration), HostExeName));
            if (File.Exists(dev)) return dev;
        }
        return null;
    }

    private static string CurrentConfiguration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    /// <summary>
    /// Start the host against <paramref name="settingsPath"/>. Any already-running child is
    /// stopped first — one central can hold the device, so two hosts is never the intent.
    /// </summary>
    public void Start(string? settingsPath = null)
    {
        Stop();
        LastError = null;
        IsReady = false;

        var exe = ResolveHostPath();
        if (exe is null)
        {
            LastError = $"{HostExeName} not found. Build Project/ZeroWearable, or reinstall " +
                        "AgentZero Lite to get the shipped copy.";
            AppLogger.Log($"[Wearable] {LastError}");
            StatusChanged?.Invoke();
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Redirected stdin is also the signal: the host checks Console.IsInputRedirected
            // and runs headless instead of opening its interactive prompt.
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(settingsPath ?? WearableSettingsStore.DefaultFilePath);

        try
        {
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            process.Exited += (_, _) => OnExited(process);

            if (!process.Start())
            {
                LastError = $"could not start {exe}";
                StatusChanged?.Invoke();
                return;
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            // Right after Start, before anything can go wrong: from here on the host cannot
            // outlive us, however we go.
            _job.Adopt(process.Handle);

            lock (_lock) _process = process;
            IsRunning = true;
            ProcessId = process.Id;
            AppLogger.Log($"[Wearable] host started, pid {process.Id}: {exe}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLogger.Log($"[Wearable] host failed to start: {ex}");
        }
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Stop the host. Kills the tree rather than asking politely: the child has no window and
    /// no console of its own to send Ctrl+C to, and Windows releases the BLE handles on exit —
    /// which is what actually matters, because the watch only advertises while unconnected.
    /// </summary>
    public void Stop()
    {
        Process? process;
        lock (_lock)
        {
            process = _process;
            _process = null;
        }
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Wearable] stop: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }

        IsRunning = false;
        IsReady = false;
        ProcessId = null;
        StatusChanged?.Invoke();
    }

    private void OnLine(string? line)
    {
        if (line is null) return;
        LineReceived?.Invoke(line);

        if (!IsReady && line.StartsWith("[host/ready]", StringComparison.Ordinal))
        {
            IsReady = true;
            StatusChanged?.Invoke();
        }
        // Errors are surfaced on the panel rather than only in the log tail, which the user
        // may have scrolled away from by the time they wonder why nothing speaks.
        else if (line.Contains("/error]", StringComparison.Ordinal))
        {
            LastError = line;
            StatusChanged?.Invoke();
        }
    }

    private void OnExited(Process process)
    {
        bool ours;
        lock (_lock) ours = ReferenceEquals(_process, process);
        if (!ours) return;   // superseded by a restart; that Start already reported state

        lock (_lock) _process = null;
        IsRunning = false;
        IsReady = false;
        ProcessId = null;
        if (process.ExitCode != 0)
            LastError = $"host exited with code {process.ExitCode}";
        AppLogger.Log($"[Wearable] host exited, code {process.ExitCode}");
        StatusChanged?.Invoke();
    }
}
