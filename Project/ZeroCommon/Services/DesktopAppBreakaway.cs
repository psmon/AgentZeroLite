using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Agent.Common.Services;

/// <summary>
/// Keeps the processes an MSIX-packaged AgentZero starts out of the package's private
/// AppData store.
///
/// <para>A packaged app's writes to <c>%LOCALAPPDATA%</c>, <c>%APPDATA%</c> and HKCU that
/// create something new are redirected into a per-package store — and by default so are
/// the writes of every process it starts. For a terminal host that is wrong: <c>npm install
/// -g</c> run in a tab would land in the package's cache, invisible to the user's other
/// terminals and deleted on uninstall. The manifest switch that turns virtualization off
/// (<c>unvirtualizedResources</c>) is a restricted capability the Store denied (policy
/// 10.6.3), so the processes are created with the desktop-app breakaway policy instead.</para>
///
/// <para>Measured inside a registered package (Windows 11 26200): a <c>cmd</c> created with
/// <see cref="EnableProcessTree"/> made a new folder, and so did a <c>cmd</c> it started —
/// both at the real path. Without the attribute both landed in
/// <c>Packages\&lt;family&gt;\LocalCache</c>.</para>
///
/// <para>Outside a package (the regular install, the WPF host, tests) nothing is applied:
/// <see cref="IsPackagedProcess"/> is false and every caller takes its old path.</para>
/// </summary>
public static class DesktopAppBreakaway
{
    /// <summary><c>PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY</c>.</summary>
    public const int ProcThreadAttributeDesktopAppPolicy = 0x00020012;

    /// <summary><c>PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE</c>.</summary>
    public const uint EnableProcessTree = 0x01;

    private const int AppModelErrorNoPackage = 15700;
    private static readonly Lazy<bool> Packaged = new(Probe);

    /// <summary>True when this process runs with package identity (installed from the Store / MSIX).</summary>
    public static bool IsPackagedProcess() => Packaged.Value;

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var length = 0;
            var rc = GetCurrentPackageFullName(ref length, null);
            return rc != AppModelErrorNoPackage;   // ERROR_INSUFFICIENT_BUFFER means "has one"
        }
        catch (EntryPointNotFoundException) { return false; }   // pre-Windows 8
    }

    /// <summary>
    /// A native DWORD holding <see cref="EnableProcessTree"/>, for an attribute list built by
    /// the caller (the ConPTY host adds it beside the pseudo-console attribute). The memory
    /// must outlive <c>CreateProcess</c>; free it with <see cref="Marshal.FreeHGlobal"/>.
    /// </summary>
    public static IntPtr AllocPolicyValue()
    {
        var value = Marshal.AllocHGlobal(sizeof(uint));
        Marshal.WriteInt32(value, (int)EnableProcessTree);
        return value;
    }

    /// <summary>
    /// Run a command line with the breakaway policy, both output streams merged and
    /// reported line by line, stdin closed — the shape <see cref="AgentCliTools.RunInstallAsync"/>
    /// needs, which <see cref="System.Diagnostics.Process"/> cannot provide because it does
    /// not take a process attribute list.
    /// </summary>
    /// <returns>The exit code, or -1 when the process could not be started.</returns>
    public static async Task<int> RunCapturedAsync(
        string commandLine, Action<string>? onOutput, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        var inheritable = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        if (!CreatePipe(out var outRead, out var outWrite, ref inheritable, 0)) return Fail("CreatePipe(out)");
        if (!CreatePipe(out var inRead, out var inWrite, ref inheritable, 0)) return Fail("CreatePipe(in)");
        SetHandleInformation(outRead, HandleFlagInherit, 0);   // our ends stay ours
        SetHandleInformation(inWrite, HandleFlagInherit, 0);

        var attrSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        var attrList = Marshal.AllocHGlobal(attrSize);
        var policy = AllocPolicyValue();
        PROCESS_INFORMATION pi = default;
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize)) return Fail("InitializeProcThreadAttributeList");
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)ProcThreadAttributeDesktopAppPolicy, policy,
                    (IntPtr)sizeof(uint), IntPtr.Zero, IntPtr.Zero)) return Fail("UpdateProcThreadAttribute");

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.StartupInfo.dwFlags = StartfUseStdHandles;
            si.StartupInfo.hStdInput = inRead;
            si.StartupInfo.hStdOutput = outWrite;
            si.StartupInfo.hStdError = outWrite;
            si.lpAttributeList = attrList;

            if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true,
                    ExtendedStartupInfoPresent | CreateNoWindow, IntPtr.Zero, null, ref si, out pi))
                return Fail("CreateProcess");
        }
        finally
        {
            // The child has its copies; closing ours is what makes the output pipe reach EOF
            // and leaves the child's stdin at EOF from the start.
            CloseHandle(outWrite);
            CloseHandle(inRead);
            CloseHandle(inWrite);
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            Marshal.FreeHGlobal(policy);
        }

        CloseHandle(pi.hThread);
        using var processHandle = new SafeWaitHandle(pi.hProcess, ownsHandle: true);

        var reader = Task.Run(() =>
        {
            using var stream = new FileStream(new SafeFileHandle(outRead, ownsHandle: true), FileAccess.Read);
            using var text = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = text.ReadLine()) is not null) onOutput?.Invoke(line);
        });

        using var exited = new ManualResetEvent(false) { SafeWaitHandle = processHandle };
        var wait = new TaskCompletionSource();
        var registration = ThreadPool.RegisterWaitForSingleObject(exited, (_, _) => wait.TrySetResult(), null, Timeout.Infinite, true);
        try
        {
            await wait.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { System.Diagnostics.Process.GetProcessById(pi.dwProcessId).Kill(entireProcessTree: true); } catch { }
            onOutput?.Invoke("Cancelled.");
            return -1;
        }
        finally
        {
            registration.Unregister(null);
        }

        await reader.ConfigureAwait(false);
        return GetExitCodeProcess(processHandle, out var code) ? (int)code : -1;

        int Fail(string what)
        {
            onOutput?.Invoke($"Could not start the installer ({what} failed: {Marshal.GetLastWin32Error()}).");
            return -1;
        }
    }

    // ── native ──────────────────────────────────────────────────────────────

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue,
        IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeWaitHandle hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
