using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentOne.Processes;

/// <summary>
/// Every descendant of a process, read from one snapshot of the whole process
/// table BEFORE anything is killed, then killed one by one.
///
/// Neither of the two ready-made kills is enough on its own. Measured
/// (2026-10-06, Flask with debug=True through the Python install manager's
/// <c>python.exe</c> alias): the alias starts a packaged app, which breaks away
/// from our Job Object, so terminating the job ended only the shell; and
/// <c>Process.Kill(entireProcessTree)</c> matches children by their parent's
/// start time, which cannot be read once that parent is dead — so killing the
/// reloader's parent hid the reloader's child, which kept port 5000 and the
/// output pipe. A parent-id walk over a snapshot taken first has neither gap.
/// </summary>
internal static class ProcessTree
{
    /// <summary>Every process below <paramref name="root"/>, deepest last. Empty when the table cannot be read.</summary>
    public static IReadOnlyList<int> Descendants(int root)
    {
        var parents = Table();
        var found = new List<int>();
        var queue = new Queue<int>([root]);
        var seen = new HashSet<int> { root };

        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var (pid, ppid) in parents)
            {
                if (ppid != parent || !seen.Add(pid)) continue;
                found.Add(pid);
                queue.Enqueue(pid);
            }
        }
        return found;
    }

    /// <summary>Kills each pid, ignoring the ones already gone.</summary>
    public static void Kill(IEnumerable<int> pids)
    {
        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                           or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
    }

    private static List<(int Pid, int Parent)> Table() =>
        OperatingSystem.IsWindows() ? WindowsTable() : PosixTable();

    private static List<(int, int)> WindowsTable()
    {
        var rows = new List<(int, int)>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return rows;

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snapshot, ref entry) == 0) return rows;
            do rows.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID));
            while (Process32NextW(snapshot, ref entry) != 0);
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
        return rows;
    }

    /// <summary><c>ps</c> is on every macOS and Linux this ships for; /proc is not on macOS.</summary>
    private static List<(int, int)> PosixTable()
    {
        var rows = new List<(int, int)>();
        try
        {
            var start = new ProcessStartInfo("ps", "-A -o pid= -o ppid=")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            using var ps = Process.Start(start);
            if (ps is null) return rows;
            var text = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(5_000);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out var pid) && int.TryParse(parts[1], out var ppid))
                    rows.Add((pid, ppid));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
        return rows;
    }

    private const uint TH32CS_SNAPPROCESS = 0x2;

    // PROCESSENTRY32W, kept blittable: the 260-char exe name is never read, so
    // it is left as padding inside an explicit size (568 bytes on 64-bit).
    [StructLayout(LayoutKind.Sequential, Size = 568)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CloseHandle(IntPtr handle);
}
