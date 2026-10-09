using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Agent.Common.Wearable;

/// <summary>
/// A GUI's handle on the wearable host — <c>AgentZeroWearable.exe</c>, the second process
/// that owns the watch's BLE link (Windows-only: the BLE central is WinRT). Start / Stop /
/// <see cref="IsRunning"/> / <see cref="IsReady"/> / <see cref="StatusChanged"/>; nothing
/// is configured here, because the host reads <c>wearable-settings.json</c>,
/// <c>voice-settings.json</c> and <c>llm-settings.json</c> itself — "restart after a
/// settings change" is the whole protocol between the two processes.
///
/// <para>This is the WPF host's <c>WearableHostProcess</c> moved down so the Avalonia host
/// can use it (conversion work leaves the WPF copy alone, and the different name keeps WPF
/// files that import both namespaces unambiguous). The one thing a host passes in is its
/// build configuration, for the dev-layout lookup.</para>
///
/// <para>The host writes <c>[category/level] message</c> lines; <c>[host/ready]</c> means
/// the radio is up, and any <c>/error]</c> line becomes <see cref="LastError"/> so a panel
/// can show it where it will be seen rather than only in the log tail.</para>
/// </summary>
public sealed class WearableHostLauncher
{
    public const string ShippedSubdir = "wearable";
    public const string HostExeName = "AgentZeroWearable.exe";

    /// <summary>
    /// The repo's build output, so F5 works without an install step. Both GUI hosts build to
    /// <c>Project/&lt;host&gt;/bin/&lt;cfg&gt;/&lt;tfm&gt;/</c>, four levels below <c>Project</c>.
    /// </summary>
    private static readonly string DevRelativeDir =
        Path.Combine("..", "..", "..", "..", "ZeroWearable", "bin", "{0}", "net10.0-windows10.0.19041.0");

    private readonly object _lock = new();
    private readonly string _configuration;
    private readonly ChildProcessJob? _job;
    private Process? _process;

    /// <param name="configuration">"Debug" or "Release" — tried first in the dev layout.</param>
    public WearableHostLauncher(string configuration)
    {
        _configuration = configuration;
        if (OperatingSystem.IsWindows()) _job = new ChildProcessJob();
    }

    /// <summary>The host runs on Windows only (WinRT BLE).</summary>
    public static bool Supported => OperatingSystem.IsWindows();

    /// <summary>Raised on a background thread when running state, readiness or last error changes.</summary>
    public event Action? StatusChanged;

    /// <summary>Raised per stdout/stderr line, on a background thread.</summary>
    public event Action<string>? LineReceived;

    public bool IsRunning { get; private set; }
    public bool IsReady { get; private set; }
    public int? ProcessId { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>The exe this would start, or null when it is neither shipped nor built.</summary>
    public string? ResolveHostPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var shipped = Path.Combine(baseDir, ShippedSubdir, HostExeName);
        if (File.Exists(shipped)) return shipped;

        foreach (var configuration in new[] { _configuration, "Debug", "Release" }.Distinct())
        {
            var dev = Path.GetFullPath(Path.Combine(baseDir, string.Format(DevRelativeDir, configuration), HostExeName));
            if (File.Exists(dev)) return dev;
        }
        return null;
    }

    /// <summary>Starts the host; a running one is stopped first — one central can hold the device.</summary>
    public void Start(string? settingsPath = null)
    {
        Stop();
        LastError = null;
        IsReady = false;

        if (!Supported)
        {
            LastError = "the wearable host runs on Windows only (its BLE central is WinRT)";
            StatusChanged?.Invoke();
            return;
        }
        var exe = ResolveHostPath();
        if (exe is null)
        {
            LastError = $"{HostExeName} not found. Build Project/ZeroWearable, or reinstall AgentZero Lite to get the shipped copy.";
            AppLogger.Log($"[Wearable] {LastError}");
            StatusChanged?.Invoke();
            return;
        }

        var psi = HostStartInfo(exe, settingsPath);
        // Redirected stdin is also the signal: the host checks Console.IsInputRedirected and
        // runs headless instead of opening its interactive prompt.
        psi.RedirectStandardInput = true;

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
            // Before anything else can go wrong: from here on the host cannot outlive us.
            _job?.Adopt(process.Handle);

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
    /// Kills the tree rather than asking politely: the child has no console to receive
    /// Ctrl+C, and what matters is that Windows releases the BLE handles on exit.
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
        catch (Exception ex) { AppLogger.Log($"[Wearable] stop: {ex.Message}"); }
        finally { process.Dispose(); }

        IsRunning = false;
        IsReady = false;
        ProcessId = null;
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Runs the host once with extra arguments (<c>--ask</c>, <c>--speak</c>…) and returns
    /// everything it printed. Those paths touch neither the radio nor the single-instance
    /// mutex, so this is safe while the real host is up.
    /// </summary>
    public async Task<string> RunOnceAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var exe = ResolveHostPath() ?? throw new InvalidOperationException($"{HostExeName} not found - build Project/ZeroWearable");
        var psi = HostStartInfo(exe, null);
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* gone */ }
            if (ct.IsCancellationRequested) throw;
            return await stdout + await stderr + $"\n[panel/error] the run did not finish within {timeout.TotalSeconds:0} s";
        }
        return await stdout + await stderr;
    }

    private static ProcessStartInfo HostStartInfo(string exe, string? settingsPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(settingsPath ?? WearableSettingsStore.DefaultFilePath);
        return psi;
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
        else if (line.Contains("/error]", StringComparison.Ordinal))
        {
            LastError = line;
            StatusChanged?.Invoke();
        }
    }

    private void OnExited(Process process)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_process, process)) return;   // superseded by a restart
            _process = null;
        }
        IsRunning = false;
        IsReady = false;
        ProcessId = null;
        if (process.ExitCode != 0) LastError = $"host exited with code {process.ExitCode}";
        AppLogger.Log($"[Wearable] host exited, code {process.ExitCode}");
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// A Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: when this process ends —
    /// crash, "End task", <c>Stop-Process -Force</c> included — the kernel closes the last
    /// handle and terminates the host. Without it an orphaned host keeps the BLE link and
    /// :8765, and the next run's host refuses to start on its single-instance mutex.
    /// </summary>
    private sealed class ChildProcessJob
    {
        private readonly nint _handle;

        public ChildProcessJob()
        {
            _handle = CreateJobObjectW(0, null);
            if (_handle == 0)
            {
                AppLogger.Log("[Wearable] job object unavailable: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                return;
            }
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
            };
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
                {
                    AppLogger.Log("[Wearable] job limit rejected: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    CloseHandle(_handle);
                    _handle = 0;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public void Adopt(nint processHandle)
        {
            if (_handle == 0 || processHandle == 0) return;
            if (!AssignProcessToJobObject(_handle, processHandle))
                AppLogger.Log("[Wearable] could not assign the host to the job: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        // Classic DllImport: ZeroCommon does not allow unsafe code, which LibraryImport needs.
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(nint hJob, int infoClass, nint lpInfo, uint cbInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint hObject);
    }
}
