using System.ComponentModel;
using System.Runtime.InteropServices;

using Agent.Common;

namespace AgentZeroWpf.Services.Wearable;

/// <summary>
/// A Windows Job Object that kills the wearable host when AgentZeroLite dies — including
/// when it dies badly.
///
/// <para>Stopping the host from the panel already works, and so does the normal shutdown
/// path. This covers the case neither can: a crash, a Task Manager "End task", or a
/// <c>Stop-Process -Force</c>. Without it the child survives its parent while still holding
/// the BLE link and :8765, so the watch stays claimed by a process nobody can see, the next
/// GUI's host refuses to start on its single-instance mutex, and the whole thing looks like
/// "the HUD stopped working".</para>
///
/// <para><c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> makes the kernel do it: when the last
/// handle to the job closes — which happens however the process ends — every process still
/// assigned to the job is terminated. Nothing to run at exit, nothing to get skipped.</para>
/// </summary>
internal sealed partial class WearableHostJob : IDisposable
{
    private nint _handle;

    /// <summary>True when the job exists and the limit was accepted; false disables the feature
    /// (the host is then merely stopped the ordinary ways).</summary>
    public bool IsValid => _handle != 0;

    public WearableHostJob()
    {
        // Unnamed: this job belongs to this process instance and nothing else should join it.
        _handle = CreateJobObjectW(0, null);
        if (_handle == 0)
        {
            AppLogger.Log("[Wearable] job object unavailable: " +
                          new Win32Exception(Marshal.GetLastWin32Error()).Message);
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
                AppLogger.Log("[Wearable] job limit rejected: " +
                              new Win32Exception(Marshal.GetLastWin32Error()).Message);
                Dispose();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Put a just-started process in the job. Safe to call on a process that has already
    /// exited (it simply fails), and harmless when the job could not be created.
    /// </summary>
    public void Adopt(nint processHandle)
    {
        if (!IsValid || processHandle == 0) return;
        if (!AssignProcessToJobObject(_handle, processHandle))
        {
            // Not fatal: the host still runs, it just is not tied to our lifetime any more.
            AppLogger.Log("[Wearable] could not assign the host to the job: " +
                          new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
    }

    public void Dispose()
    {
        if (_handle == 0) return;
        CloseHandle(_handle);   // last handle closed → the kernel terminates the host
        _handle = 0;
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

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

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint hJob, int infoClass, nint lpInfo, uint cbInfoLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);
}
