using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AgentZeroAvalonia.Cli;

/// <summary>
/// A WinExe has no console; when run as <c>-cli</c> from a shell it attaches to the
/// parent's, and allocates one if there is none — the WPF host's
/// <c>CliHandler.AttachOrAllocConsole</c>, fenced to Windows. Redirected stdio (a
/// script, the wearable host's bridge) is left alone. macOS/Linux: stdout just works.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsConsole
{
    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE = -11;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    public static void Attach()
    {
        var existing = GetStdHandle(STD_OUTPUT_HANDLE);
        var hasStdOut = existing != IntPtr.Zero && existing != new IntPtr(-1);
        if (!hasStdOut)
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
                AllocConsole();
        }
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
    }

    public static void Detach()
    {
        try { Console.Out.Flush(); Console.Error.Flush(); } catch { }
        try { FreeConsole(); } catch { }
    }
}
