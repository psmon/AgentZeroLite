using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AgentZeroWpf.Services;

/// <summary>
/// A self-contained managed ConPTY host. Spawns a child process attached to a
/// Windows pseudo-console (conpty) and exposes its raw stdout/stderr byte stream
/// (decoded to UTF-16 text, ANSI/VT preserved) plus a stdin write channel and a
/// resize call.
///
/// This is the low layer of the <c>WebViewXterm</c> terminal backend — the piece
/// that <c>EasyWindowsTerminalControl</c>'s <c>TermPTY</c> hid inside a closed
/// NuGet. Because it is pure Win32 P/Invoke it lives WPF-side (ZeroCommon must
/// stay Win32-free), alongside <see cref="ConPtyTerminalSession"/>.
///
/// Ownership: <see cref="WebViewXtermTerminalSession"/> feeds <see cref="Output"/>
/// into two sinks — the xterm.js renderer AND an accumulated console log that
/// reproduces the shape the approval parser / event stream already consume.
/// </summary>
public sealed class ManagedConPtyHost : IDisposable
{
    /// <summary>Raised on a background thread with each decoded output chunk (VT preserved).</summary>
    public event Action<string>? Output;

    /// <summary>Raised on a background thread when the child's output pipe reaches EOF (process exit).</summary>
    public event Action? Exited;

    private readonly object _sync = new();

    private IntPtr _hPC = IntPtr.Zero;          // pseudo-console handle
    private IntPtr _attrList = IntPtr.Zero;      // proc-thread attribute list
    private IntPtr _inWrite = IntPtr.Zero;       // our end of stdin (we write)
    private IntPtr _outRead = IntPtr.Zero;       // our end of stdout (we read)
    private SafeFileHandle? _processHandle;
    private SafeFileHandle? _threadHandle;
    private Thread? _readThread;
    private volatile bool _running;
    private bool _disposed;
    private bool _wroteOnce;

    public bool IsRunning => _running;

    /// <summary>Diagnostic breadcrumb of the last Start() — HRESULTs, sizes, PID, last errors.</summary>
    public string Diagnostics { get; private set; } = "";

    /// <summary>
    /// Start the child process attached to a fresh pseudo-console.
    /// </summary>
    /// <param name="commandLine">Full command line, e.g. <c>cmd.exe /c "…"</c>. Mutated in place by CreateProcess, so a copy is made.</param>
    /// <param name="workingDir">Initial working directory, or null for the parent's.</param>
    /// <param name="cols">Initial column count.</param>
    /// <param name="rows">Initial row count.</param>
    public void Start(string commandLine, string? workingDir, short cols, short rows)
    {
        lock (_sync)
        {
            if (_running) throw new InvalidOperationException("ConPTY host already started.");
            if (_disposed) throw new ObjectDisposedException(nameof(ManagedConPtyHost));

            // Two anonymous pipes: one for input (we write → child reads), one
            // for output (child writes → we read).
            if (!Native.CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0))
                throw new InvalidOperationException($"CreatePipe(input) failed: {Marshal.GetLastWin32Error()}");
            if (!Native.CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
                throw new InvalidOperationException($"CreatePipe(output) failed: {Marshal.GetLastWin32Error()}");

            var size = new Native.COORD { X = Math.Max((short)1, cols), Y = Math.Max((short)1, rows) };
            int hr = Native.CreatePseudoConsole(size, inRead, outWrite, 0, out _hPC);
            Diagnostics = $"CreatePseudoConsole hr=0x{hr:X8} hPC=0x{_hPC:X} size={size.X}x{size.Y}";
            if (hr != 0)
                throw new InvalidOperationException($"CreatePseudoConsole failed: HRESULT 0x{hr:X8}");

            // The pseudo-console duplicated the child-side handles; close our
            // copies so pipe EOF works once the child exits.
            Native.CloseHandle(inRead);
            Native.CloseHandle(outWrite);

            // Build the proc-thread attribute list carrying the pseudo-console.
            var lpSize = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
            Diagnostics += $" | attrListSize={lpSize}";
            _attrList = Marshal.AllocHGlobal(lpSize);
            if (!Native.InitializeProcThreadAttributeList(_attrList, 1, 0, ref lpSize))
                throw new InvalidOperationException($"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");
            if (!Native.UpdateProcThreadAttribute(
                    _attrList, 0,
                    (IntPtr)Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException($"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");
            Diagnostics += " | attrOK";

            var startupInfo = new Native.STARTUPINFOEX();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
            startupInfo.lpAttributeList = _attrList;

            // CreateProcess mutates lpCommandLine — hand it a private mutable buffer.
            var cmdBuffer = new StringBuilder(commandLine);
            bool ok = Native.CreateProcess(
                null, cmdBuffer, IntPtr.Zero, IntPtr.Zero,
                bInheritHandles: false,
                dwCreationFlags: Native.EXTENDED_STARTUPINFO_PRESENT,
                lpEnvironment: IntPtr.Zero,
                lpCurrentDirectory: workingDir,
                ref startupInfo,
                out var procInfo);
            Diagnostics += $" | CreateProcess ok={ok} pid={procInfo.dwProcessId} lastErr={Marshal.GetLastWin32Error()}";
            if (!ok)
                throw new InvalidOperationException($"CreateProcess failed: {Marshal.GetLastWin32Error()}");

            _processHandle = new SafeFileHandle(procInfo.hProcess, ownsHandle: true);
            _threadHandle = new SafeFileHandle(procInfo.hThread, ownsHandle: true);

            _inWrite = inWrite;
            _outRead = outRead;

            _running = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "ConPtyReadLoop" };
            _readThread.Start();
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        // Streaming UTF-8 decoder — carries partial multibyte sequences across reads.
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[8192];
        var handle = _outRead;

        try
        {
            while (true)
            {
                // Blocking ReadFile on the pipe read end — returns false with
                // ERROR_BROKEN_PIPE (109) when the child + ConPTY close their
                // write ends (process exit), which we treat as EOF.
                if (!Native.ReadFile(handle, buffer, (uint)buffer.Length, out uint n, IntPtr.Zero) || n == 0)
                    break;

                int charCount = decoder.GetChars(buffer, 0, (int)n, chars, 0, flush: false);
                if (charCount > 0)
                {
                    var text = new string(chars, 0, charCount);
                    try { Output?.Invoke(text); } catch { /* subscriber bug, not load-bearing */ }
                }
            }
        }
        catch
        {
            // Pipe broken on shutdown — treated as EOF below.
        }
        finally
        {
            _running = false;
            try { Exited?.Invoke(); } catch { }
        }
    }

    /// <summary>Write text to the child's stdin as UTF-8. VT/control bytes pass through verbatim.</summary>
    public void Write(ReadOnlySpan<char> text)
    {
        if (!_running || text.Length == 0) return;
        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        lock (_sync)
        {
            if (_inWrite == IntPtr.Zero) return;
            try
            {
                bool wok = Native.WriteFile(_inWrite, bytes, (uint)bytes.Length, out uint wrote, IntPtr.Zero);
                if (!_wroteOnce) { _wroteOnce = true; Diagnostics += $" | WriteFile ok={wok} wrote={wrote} err={Marshal.GetLastWin32Error()}"; }
            }
            catch
            {
                // Pipe may have broken — the health state machine surfaces the wedge.
            }
        }
    }

    /// <summary>Block until the child process exits or the timeout elapses.
    /// Uses the process handle (ConPTY keeps the output pipe open past child
    /// exit, so pipe-EOF is not a reliable exit signal). Returns true if exited.</summary>
    public bool WaitForProcessExit(int milliseconds)
    {
        var h = _processHandle;
        if (h is null || h.IsInvalid) return true;
        return Native.WaitForSingleObject(h, (uint)milliseconds) == 0; // WAIT_OBJECT_0
    }

    /// <summary>Resize the pseudo-console viewport.</summary>
    public void Resize(short cols, short rows)
    {
        lock (_sync)
        {
            if (_hPC == IntPtr.Zero) return;
            var size = new Native.COORD { X = Math.Max((short)1, cols), Y = Math.Max((short)1, rows) };
            try { Native.ResizePseudoConsole(_hPC, size); } catch { }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;

            // Close stdin first (signals EOF to the child), then tear down the
            // pseudo-console (terminates the child), then the read side.
            if (_inWrite != IntPtr.Zero) { try { Native.CloseHandle(_inWrite); } catch { } _inWrite = IntPtr.Zero; }
            if (_hPC != IntPtr.Zero)
            {
                try { Native.ClosePseudoConsole(_hPC); } catch { }
                _hPC = IntPtr.Zero;
            }
            if (_outRead != IntPtr.Zero) { try { Native.CloseHandle(_outRead); } catch { } _outRead = IntPtr.Zero; }

            if (_attrList != IntPtr.Zero)
            {
                try { Native.DeleteProcThreadAttributeList(_attrList); } catch { }
                try { Marshal.FreeHGlobal(_attrList); } catch { }
                _attrList = IntPtr.Zero;
            }

            try { _processHandle?.Dispose(); } catch { }
            try { _threadHandle?.Dispose(); } catch { }
        }

        // Join outside the lock — the read thread grabs _sync on exit paths.
        try { _readThread?.Join(TimeSpan.FromMilliseconds(500)); } catch { }
    }

    // ── Win32 ConPTY interop (classic DllImport — attribute lists + CreateProcess
    //    marshal more cleanly here than via source-generated LibraryImport) ──
    private static class Native
    {
        public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        public const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

        [StructLayout(LayoutKind.Sequential)]
        public struct COORD { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(SafeFileHandle hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue,
            IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcess(
            string? lpApplicationName, StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
    }
}
