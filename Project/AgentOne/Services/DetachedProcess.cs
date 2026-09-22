using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentOne.Services;

/// <summary>
/// Starts a child that shares nothing with the caller's console or pipes.
///
/// <see cref="Process.Start(ProcessStartInfo)"/> on Windows always creates the
/// child with handle inheritance on, so it receives every inheritable handle
/// the caller holds — not just stdin/out/err but the pipes a shell or another
/// agent wired around the caller. A daemon started that way keeps those pipes
/// open, and `agent-one session start | cat` blocks until the daemon stops
/// (measured: 3m55s, released the moment `session stop` ran). Windows goes
/// through CreateProcessW with bInheritHandles = FALSE; elsewhere fds are
/// close-on-exec by default and redirecting the three standard streams —
/// then dropping them — is enough.
/// </summary>
public static class DetachedProcess
{
    /// <summary>Starts <paramref name="exe"/> detached; the returned process is only for pid / exit checks.</summary>
    public static Process Start(string exe, IReadOnlyList<string> args, string workingDirectory)
    {
        if (OperatingSystem.IsWindows()) return StartWindows(exe, args, workingDirectory);

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var a in args) start.ArgumentList.Add(a);

        var child = Process.Start(start) ?? throw new InvalidOperationException("no process");
        child.StandardInput.Close();
        child.StandardOutput.Close();
        child.StandardError.Close();
        return child;
    }

    // ---------------------------------------------------------------- windows

    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateNewProcessGroup = 0x00000200;   // the caller's Ctrl+C is not the daemon's
    private const uint CreateUnicodeEnvironment = 0x00000400;

    private static Process StartWindows(string exe, IReadOnlyList<string> args, string workingDirectory)
    {
        var commandLine = new StringBuilder();
        Quote(commandLine, exe);
        foreach (var a in args) Quote(commandLine.Append(' '), a);

        var si = new STARTUPINFOW { cb = (uint)Marshal.SizeOf<STARTUPINFOW>() };
        if (!CreateProcessW(exe, commandLine.ToString(), 0, 0, false,
                CreateNoWindow | CreateNewProcessGroup | CreateUnicodeEnvironment,
                0, workingDirectory, ref si, out var pi))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return Process.GetProcessById((int)pi.dwProcessId);
    }

    /// <summary>
    /// The quoting CommandLineToArgvW undoes: quotes around anything with a
    /// space or a quote, backslashes doubled only where they precede a quote.
    /// </summary>
    public static void Quote(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) { sb.Append(arg); return; }

        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string lpApplicationName, string lpCommandLine, nint lpProcessAttributes, nint lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, nint lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
