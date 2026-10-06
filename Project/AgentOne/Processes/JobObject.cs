using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentOne.Processes;

/// <summary>
/// A Windows job with KILL_ON_JOB_CLOSE around one started shell: every
/// process the shell starts joins it, <see cref="Terminate"/> ends all of them
/// at once, and closing the handle — including this process dying — ends
/// whatever is left. <c>Process.Kill(entireProcessTree)</c> walks parent ids
/// at kill time and was measured to miss one: a Flask reloader child survived
/// with port 5000 and the output pipe, and the turn waited on that pipe for
/// good. Elsewhere this is a no-op and the tree kill is all there is.
///
/// The shell is assigned right after it starts, before it has parsed its
/// command line, so its children are created inside the job.
/// </summary>
internal sealed class JobObject : IDisposable
{
    private IntPtr _handle;

    private JobObject(IntPtr handle) => _handle = handle;

    /// <summary>A job holding <paramref name="process"/>, or null where there are none (or the call failed).</summary>
    public static JobObject? For(Process process)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return null;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var job = new JobObject(handle);

        try
        {
            if (SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info,
                    (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()) == 0
                || AssignProcessToJobObject(handle, process.Handle) == 0)
            {
                job.Dispose();
                return null;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            job.Dispose();
            return null;
        }

        return job;
    }

    /// <summary>Ends every process in the job.</summary>
    public void Terminate()
    {
        if (_handle != IntPtr.Zero) _ = TerminateJobObject(_handle, 1);
    }

    public void Dispose()
    {
        var handle = _handle;
        _handle = IntPtr.Zero;
        if (handle != IntPtr.Zero) _ = CloseHandle(handle);
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
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
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CloseHandle(IntPtr handle);
}
