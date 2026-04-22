using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgentZeroWpf.Module;
using Microsoft.Win32.SafeHandles;

namespace AgentZeroWpf;

internal static class CliHandler
{
    // Must match MainWindow.xaml `Title` attribute — FindWindow looks up by exact title.
    private const string MainWindowTitle = "AgentZero Lite";

    // === Global CLI Options ===
    private static bool _noWait = false;
    private static int _timeoutMs = 5000;   // default 5 seconds
    private const int PollIntervalMs = 300; // polling interval for MMF read

    public static int Run(string[] args)
    {
        AttachOrAllocConsole();

        // args after "-cli"
        var cliArgs = args.SkipWhile(a => !a.Equals("-cli", StringComparison.OrdinalIgnoreCase))
                         .Skip(1)
                         .ToList();

        if (cliArgs.Count == 0)
        {
            PrintUsage();
            return 0;
        }

        // Parse global options (reverse iteration for safe removal)
        for (int i = cliArgs.Count - 1; i >= 0; i--)
        {
            if (cliArgs[i].Equals("--no-wait", StringComparison.OrdinalIgnoreCase))
            {
                _noWait = true;
                cliArgs.RemoveAt(i);
            }
            else if (cliArgs[i].Equals("--timeout", StringComparison.OrdinalIgnoreCase)
                     && i + 1 < cliArgs.Count)
            {
                if (int.TryParse(cliArgs[i + 1], out int t))
                    _timeoutMs = t;
                cliArgs.RemoveAt(i + 1);
                cliArgs.RemoveAt(i);
            }
        }

        if (cliArgs.Count == 0)
        {
            PrintUsage();
            return 0;
        }

        var command = cliArgs[0].ToLowerInvariant();

        return command switch
        {
            "help" or "--help" or "-h" or "/?" => PrintHelp(),
            "status" => GetStatus(),
            "copy" => CopyToClipboard(),
            "open-win" => OpenWin(),
            "close-win" => CloseWin(),
            "console" => OpenConsole(),
            "log" => ShowLog(cliArgs.Skip(1).ToArray()),
            "terminal-list" => TerminalList(),
            "terminal-send" => TerminalSend(cliArgs.Skip(1).ToArray()),
            "terminal-key" => TerminalKey(cliArgs.Skip(1).ToArray()),
            "terminal-read" => TerminalRead(cliArgs.Skip(1).ToArray()),
            "bot-chat" => BotChat(cliArgs.Skip(1).ToArray()),
            _ => PrintUnknownCommand(command),
        };
    }


    private static bool SendWpfCommand(IntPtr agentWnd, string jsonCommand)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonCommand);
        var gch = GCHandle.Alloc(jsonBytes, GCHandleType.Pinned);
        try
        {
            var cds = new NativeMethods.COPYDATASTRUCT
            {
                dwData = (IntPtr)0x414C, // "AL" marker — AgentZero Lite (PRO uses "AG" 0x4147)
                cbData = jsonBytes.Length,
                lpData = gch.AddrOfPinnedObject(),
            };
            NativeMethods.SendMessageCopyData(agentWnd, NativeMethods.WM_COPYDATA, IntPtr.Zero, ref cds);
            return true;
        }
        finally
        {
            gch.Free();
        }
    }

    private static string? TryReadMmf(string mmfName, int mmfSize)
    {
        if (_noWait)
        {
            Console.WriteLine("(--no-wait) Command sent. Skipping response wait.");
            return null;
        }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < _timeoutMs)
        {
            try
            {
                using var mmf = MemoryMappedFile.OpenExisting(mmfName);
                using var accessor = mmf.CreateViewAccessor(0, mmfSize, MemoryMappedFileAccess.Read);

                int dataLen = accessor.ReadInt32(0);
                if (dataLen > 0 && dataLen <= mmfSize - 4)
                {
                    byte[] data = new byte[dataLen];
                    accessor.ReadArray(4, data, 0, dataLen);
                    return Encoding.UTF8.GetString(data);
                }
            }
            catch (FileNotFoundException)
            {
                // MMF not yet created — retry
            }

            Thread.Sleep(PollIntervalMs);
        }

        Console.Error.WriteLine(
            $"Error: No response within {_timeoutMs}ms. " +
            $"(Use --timeout N to increase, or --no-wait to skip)");
        return null;
    }

    // =========================================================================
    //  status / copy
    // =========================================================================

    private static int GetStatus()
    {
        IntPtr agentWnd = LocateAgentZeroWindow();
        if (agentWnd == IntPtr.Zero)
        {
            PrintNotRunning();
            return 1;
        }

        SendWpfCommand(agentWnd, "{\"command\":\"status\"}");

        string? json = TryReadMmf("AgentZeroLite_Status_Response", 8192);
        if (json == null) return _noWait ? 0 : 1;

        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        // Lite status schema: { "status_bar": string, "groups": int }
        // (PRO-era capture/filter/scroll fields are intentionally absent.)
        string statusBar = r.TryGetProperty("status_bar", out var sbProp)
            ? (sbProp.GetString() ?? "")
            : "";
        int groupCount = r.TryGetProperty("groups", out var grpProp) && grpProp.ValueKind == JsonValueKind.Number
            ? grpProp.GetInt32()
            : 0;

        Console.WriteLine("=== AgentZero Lite Status ===");
        Console.WriteLine();
        Console.WriteLine($"  Status Bar:   {(string.IsNullOrEmpty(statusBar) ? "(empty)" : statusBar)}");
        Console.WriteLine($"  Workspaces:   {groupCount}");
        Console.WriteLine();
        Console.WriteLine("  (Run 'terminal-list' for per-tab details.)");

        return 0;
    }

    // =========================================================================
    //  open-win / close-win
    // =========================================================================

    private static int CopyToClipboard()
    {
        IntPtr agentWnd = LocateAgentZeroWindow();
        if (agentWnd == IntPtr.Zero)
        {
            PrintNotRunning();
            return 1;
        }

        SendWpfCommand(agentWnd, "{\"command\":\"copy\"}");

        string? json = TryReadMmf("AgentZeroLite_Copy_Response", 256);
        if (json == null) return _noWait ? 0 : 1;

        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        bool copied = r.GetProperty("copied").GetBoolean();
        int length = r.GetProperty("length").GetInt32();

        if (copied)
        {
            Console.WriteLine($"Copied {length:N0} chars to clipboard.");
            return 0;
        }
        else
        {
            Console.Error.WriteLine("No captured text to copy.");
            return 1;
        }
    }

    private static int OpenWin()
    {
        IntPtr existing = LocateAgentZeroWindow();
        if (existing != IntPtr.Zero)
        {
            // Already running — bring to foreground
            NativeMethods.SetForegroundWindow(existing);
            Console.WriteLine("AgentZero Lite is already running. Brought to foreground.");
            return 0;
        }

        string exePath = Process.GetCurrentProcess().MainModule?.FileName
                         ?? Path.Combine(AppContext.BaseDirectory, "AgentZeroLite.exe");

        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,
        });

        Console.WriteLine("AgentZero WPF started.");
        return 0;
    }

    private static int OpenConsole()
    {
        string workDir = AppContext.BaseDirectory;
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = workDir,
            UseShellExecute = true,
        });
        Console.WriteLine($"PowerShell opened at: {workDir}");
        return 0;
    }

    private static int CloseWin()
    {
        IntPtr agentWnd = LocateAgentZeroWindow();
        if (agentWnd == IntPtr.Zero)
        {
            PrintNotRunning();
            return 1;
        }

        NativeMethods.PostMessage(agentWnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Console.WriteLine("Close signal sent to AgentZero WPF.");
        return 0;
    }

    // =========================================================================
    //  mousemove / mouseclick / mousewheel  (standalone — no WPF required)
    // =========================================================================






    /// <summary>Quick capture that returns text without stdout output.</summary>

    private static int ShowLog(string[] cmdArgs)
    {
        int count = 50;
        bool clear = false;

        for (int i = 0; i < cmdArgs.Length; i++)
        {
            switch (cmdArgs[i].ToLowerInvariant())
            {
                case "--last" when i + 1 < cmdArgs.Length && int.TryParse(cmdArgs[i + 1], out int n):
                    count = Math.Clamp(n, 1, 500); i++; break;
                case "--clear":
                    clear = true; break;
            }
        }

        if (clear)
        {
            CliActionLog.Trim();
            Console.WriteLine("Action log trimmed.");
            return 0;
        }

        var lines = CliActionLog.GetRecent(count);
        int total = CliActionLog.GetTotalCount();

        if (lines.Length == 0)
        {
            Console.WriteLine("No action history.");
            return 0;
        }

        Console.WriteLine($"=== Action Log (last {lines.Length} of {total}) ===");
        Console.WriteLine();
        foreach (var line in lines)
            Console.WriteLine(line);

        return 0;
    }

    // =========================================================================
    //  dpi  (standalone — display DPI and coordinate mapping info)
    // =========================================================================

    // DPI — delegated to AgentActions.GetSystemDpi()




    private static string EscapeJson(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    // =========================================================================
    //  AgentZero discovery helper
    // =========================================================================

    /// <summary>
    /// Locate the AgentZero Lite main window. First tries <c>FindWindow</c> by exact
    /// title (fast path). If that fails for any reason — title drift, window not in
    /// the normal top-level enumeration at that instant, etc. — falls back to scanning
    /// the <c>AgentZeroLite</c> processes for a live MainWindowHandle. This avoids
    /// false "not running" errors when the GUI is alive but title lookup races.
    /// Returns <see cref="IntPtr.Zero"/> if GUI genuinely isn't running; error-message
    /// printing is left to the caller.
    /// </summary>
    private static IntPtr LocateAgentZeroWindow()
    {
        IntPtr hwnd = NativeMethods.FindWindow(null, MainWindowTitle);
        if (hwnd != IntPtr.Zero) return hwnd;

        int selfPid = Environment.ProcessId;
        foreach (var proc in Process.GetProcessesByName("AgentZeroLite"))
        {
            try
            {
                if (proc.Id == selfPid) continue;   // skip the current CLI process
                var mw = proc.MainWindowHandle;
                if (mw != IntPtr.Zero) return mw;
            }
            catch
            {
                // Access denied or process exited between enumeration and probe — skip.
            }
            finally
            {
                proc.Dispose();
            }
        }
        return IntPtr.Zero;
    }

    private static void PrintNotRunning()
    {
        Console.Error.WriteLine("Error: AgentZero Lite GUI is not running.");
        Console.Error.WriteLine("Start AgentZeroLite.exe first (GUI mode), then retry.");
    }

    private static IntPtr FindAgentZero()
    {
        IntPtr hwnd = LocateAgentZeroWindow();
        if (hwnd == IntPtr.Zero) PrintNotRunning();
        return hwnd;
    }

    // =========================================================================
    //  terminal-list : query active terminal sessions
    // =========================================================================

    private const string TerminalListMmfName = "AgentZeroLite_TerminalList_Response";
    private const int TerminalListMmfSize = 32768;

    private static int TerminalList()
    {
        IntPtr agentWnd = FindAgentZero();
        if (agentWnd == IntPtr.Zero) return 1;

        SendWpfCommand(agentWnd, "{\"command\":\"terminal-list\"}");

        string? json = TryReadMmf(TerminalListMmfName, TerminalListMmfSize);
        if (json == null) return _noWait ? 0 : 1;

        // Pretty-print
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("groups", out var groups))
        {
            Console.WriteLine("=== Active Terminal Sessions ===");
            Console.WriteLine();

            foreach (var group in groups.EnumerateArray())
            {
                var gIdx = group.GetProperty("group_index").GetInt32();
                var gName = group.GetProperty("group_name").GetString() ?? "";
                var gDir = group.GetProperty("directory").GetString() ?? "";
                Console.WriteLine($"  Group {gIdx}: {gName}  ({gDir})");

                if (group.TryGetProperty("tabs", out var tabs))
                {
                    foreach (var tab in tabs.EnumerateArray())
                    {
                        var tIdx = tab.GetProperty("tab_index").GetInt32();
                        var tTitle = tab.GetProperty("title").GetString() ?? "";
                        var active = tab.GetProperty("active").GetBoolean();
                        var running = tab.GetProperty("running").GetBoolean();
                        var sessionId = tab.GetProperty("session_id").GetString() ?? "";
                        var hwnd = tab.TryGetProperty("hwnd", out var hp) ? hp.GetString() ?? "" : "";
                        var marker = active ? " *" : "";
                        var stateTag = running ? "" : " [not started]";
                        Console.WriteLine($"    Tab {tIdx}: {tTitle}{marker}{stateTag}");
                        Console.WriteLine($"      ID: {sessionId}  HWND: {(string.IsNullOrEmpty(hwnd) ? "N/A" : hwnd)}");
                    }
                }
                Console.WriteLine();
            }
        }

        // Also output raw JSON for programmatic consumption
        Console.WriteLine("--- JSON ---");
        Console.WriteLine(json);

        return 0;
    }

    // =========================================================================
    //  terminal-send <group> <tab> <text...>  : send text to a terminal
    // =========================================================================

    private const string TerminalSendMmfName = "AgentZeroLite_TerminalSend_Response";
    private const int TerminalSendMmfSize = 1024;

    private static int TerminalSend(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: terminal-send <group_index> <tab_index> <text...>");
            Console.Error.WriteLine("  Sends text + Enter to the specified terminal.");
            Console.Error.WriteLine("  Use 'terminal-list' to discover group/tab indices.");
            return 1;
        }

        if (!int.TryParse(args[0], out int groupIdx))
        {
            Console.Error.WriteLine($"Error: Invalid group_index '{args[0]}'. Must be an integer.");
            return 1;
        }
        if (!int.TryParse(args[1], out int tabIdx))
        {
            Console.Error.WriteLine($"Error: Invalid tab_index '{args[1]}'. Must be an integer.");
            return 1;
        }

        // Join remaining args as the text to send
        string text = string.Join(" ", args.Skip(2));

        IntPtr agentWnd = FindAgentZero();
        if (agentWnd == IntPtr.Zero) return 1;

        var sb = new StringBuilder();
        sb.Append("{\"command\":\"terminal-send\"");
        sb.Append($",\"group_index\":{groupIdx}");
        sb.Append($",\"tab_index\":{tabIdx}");
        sb.Append($",\"text\":\"{EscapeJson(text)}\"");
        sb.Append('}');

        SendWpfCommand(agentWnd, sb.ToString());

        string? json = TryReadMmf(TerminalSendMmfName, TerminalSendMmfSize);
        if (json == null) return _noWait ? 0 : 1;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        bool ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
        if (ok)
        {
            Console.WriteLine($"Sent to terminal [{groupIdx}:{tabIdx}]: {text}");
        }
        else
        {
            var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : "unknown";
            Console.Error.WriteLine($"Error: {error}");
            return 1;
        }

        return 0;
    }

    // =========================================================================
    //  terminal-key <group> <tab> <key>  : send raw key sequence to a terminal
    // =========================================================================

    private static int TerminalKey(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: terminal-key <group_index> <tab_index> <key>");
            Console.Error.WriteLine("  Sends a raw key sequence to the specified terminal.");
            Console.Error.WriteLine("  Supported keys:");
            Console.Error.WriteLine("    cr        - Carriage Return (\\r)");
            Console.Error.WriteLine("    lf        - Line Feed (\\n)");
            Console.Error.WriteLine("    crlf      - CR+LF (\\r\\n)");
            Console.Error.WriteLine("    esc       - Escape (\\x1B)");
            Console.Error.WriteLine("    tab       - Tab (\\t)");
            Console.Error.WriteLine("    backspace - Backspace (\\x08)");
            Console.Error.WriteLine("    del       - Delete (\\x7F)");
            Console.Error.WriteLine("    ctrlc     - Ctrl+C (\\x03)");
            Console.Error.WriteLine("    ctrld     - Ctrl+D (\\x04)");
            Console.Error.WriteLine("    up/down/left/right - Arrow keys");
            Console.Error.WriteLine("    hex:XX    - Raw hex byte (e.g. hex:0D)");
            return 1;
        }

        if (!int.TryParse(args[0], out int groupIdx))
        {
            Console.Error.WriteLine($"Error: Invalid group_index '{args[0]}'.");
            return 1;
        }
        if (!int.TryParse(args[1], out int tabIdx))
        {
            Console.Error.WriteLine($"Error: Invalid tab_index '{args[1]}'.");
            return 1;
        }

        string keyName = args[2].ToLowerInvariant();

        IntPtr agentWnd = FindAgentZero();
        if (agentWnd == IntPtr.Zero) return 1;

        var sb = new StringBuilder();
        sb.Append("{\"command\":\"terminal-key\"");
        sb.Append($",\"group_index\":{groupIdx}");
        sb.Append($",\"tab_index\":{tabIdx}");
        sb.Append($",\"key\":\"{EscapeJson(keyName)}\"");
        sb.Append('}');

        SendWpfCommand(agentWnd, sb.ToString());

        string? json = TryReadMmf(TerminalSendMmfName, TerminalSendMmfSize);
        if (json == null) return _noWait ? 0 : 1;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        bool ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
        if (ok)
        {
            Console.WriteLine($"Key sent to terminal [{groupIdx}:{tabIdx}]: {keyName}");
        }
        else
        {
            var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : "unknown";
            Console.Error.WriteLine($"Error: {error}");
            return 1;
        }

        return 0;
    }

    // =========================================================================
    //  terminal-read <group> <tab> [--last N]  : read terminal output text
    // =========================================================================

    private const string TerminalReadMmfName = "AgentZeroLite_TerminalRead_Response";
    private const int TerminalReadMmfSize = 65536; // 64KB for terminal text

    private static int TerminalRead(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: terminal-read <group_index> <tab_index> [--last N]");
            Console.Error.WriteLine("  Reads console output text from the specified terminal.");
            Console.Error.WriteLine("  --last N  Return only the last N characters (default: all)");
            return 1;
        }

        if (!int.TryParse(args[0], out int groupIdx))
        {
            Console.Error.WriteLine($"Error: Invalid group_index '{args[0]}'.");
            return 1;
        }
        if (!int.TryParse(args[1], out int tabIdx))
        {
            Console.Error.WriteLine($"Error: Invalid tab_index '{args[1]}'.");
            return 1;
        }

        int lastN = 0; // 0 = all
        for (int i = 2; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--last", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var n))
            {
                lastN = n;
                break;
            }
        }

        IntPtr agentWnd = FindAgentZero();
        if (agentWnd == IntPtr.Zero) return 1;

        var sb = new StringBuilder();
        sb.Append("{\"command\":\"terminal-read\"");
        sb.Append($",\"group_index\":{groupIdx}");
        sb.Append($",\"tab_index\":{tabIdx}");
        sb.Append($",\"last\":{lastN}");
        sb.Append('}');

        SendWpfCommand(agentWnd, sb.ToString());

        string? json = TryReadMmf(TerminalReadMmfName, TerminalReadMmfSize);
        if (json == null) return _noWait ? 0 : 1;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        bool ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
        if (ok)
        {
            var text = root.GetProperty("text").GetString() ?? "";
            Console.Write(text);
        }
        else
        {
            var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : "unknown";
            Console.Error.WriteLine($"Error: {error}");
            return 1;
        }

        return 0;
    }

    // =========================================================================
    //  bot-chat <text...>  : send chat message to AgentBot
    // =========================================================================

    private const string BotChatMmfName = "AgentZeroLite_BotChat_Response";
    private const int BotChatMmfSize = 1024;

    private static int BotChat(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: bot-chat <message...>");
            Console.Error.WriteLine("  Sends a chat message to AgentBot window.");
            Console.Error.WriteLine("  Options:");
            Console.Error.WriteLine("    --from <name>  Sender name (default: \"CLI\")");
            return 1;
        }

        // Parse --from option
        string from = "CLI";
        var textParts = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--from", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                from = args[++i];
            }
            else
            {
                textParts.Add(args[i]);
            }
        }

        string message = string.Join(" ", textParts);
        if (string.IsNullOrWhiteSpace(message))
        {
            Console.Error.WriteLine("Error: Message text is empty.");
            return 1;
        }

        IntPtr agentWnd = FindAgentZero();
        if (agentWnd == IntPtr.Zero) return 1;

        var sb = new StringBuilder();
        sb.Append("{\"command\":\"bot-chat\"");
        sb.Append($",\"from\":\"{EscapeJson(from)}\"");
        sb.Append($",\"message\":\"{EscapeJson(message)}\"");
        sb.Append('}');

        SendWpfCommand(agentWnd, sb.ToString());

        string? json = TryReadMmf(BotChatMmfName, BotChatMmfSize);
        if (json == null) return _noWait ? 0 : 1;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        bool ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
        if (ok)
        {
            Console.WriteLine($"[{from}] → AgentBot: {message}");
        }
        else
        {
            var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : "unknown";
            Console.Error.WriteLine($"Error: {error}");
            return 1;
        }

        return 0;
    }

    // =========================================================================
    //  bot-signal <kind> [--from name] [--to name] [--message text...]
    //      : send structured peer signal to AgentBot broker
    // =========================================================================

    private static int PrintHelp()
    {
        PrintUsage();
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  AgentZeroLite.ps1 terminal-list");
        Console.WriteLine("  AgentZeroLite.ps1 terminal-send 0 0 \"git status\"");
        Console.WriteLine("  AgentZeroLite.ps1 terminal-read 0 0 --last 2000");
        Console.WriteLine("  AgentZeroLite.ps1 terminal-key  0 0 Interrupt");
        Console.WriteLine("  AgentZeroLite.ps1 bot-chat \"build finished\" --from CI");
        Console.WriteLine("  AgentZeroLite.ps1 status --no-wait");
        Console.WriteLine();
        Console.WriteLine("Notes:");
        Console.WriteLine("  * The GUI must already be running. Start it via open-win if needed.");
        Console.WriteLine("  * --no-wait skips the MMF response round-trip for fire-and-forget.");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("AgentZero Lite CLI");
        Console.WriteLine();
        Console.WriteLine("Usage: AgentZeroLite.exe -cli <command> [--no-wait] [--timeout N]");
        Console.WriteLine("   or: AgentZeroLite.ps1 <command> [--no-wait] [--timeout N]");
        Console.WriteLine();
        Console.WriteLine("Global Options:");
        Console.WriteLine("  --no-wait              Fire-and-forget: send command, skip response");
        Console.WriteLine("  --timeout N            Wait up to N ms for response (default: 5000)");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  status                                  Show Lite app state");
        Console.WriteLine("  copy                                    Copy captured text to clipboard");
        Console.WriteLine("  open-win                                Launch the GUI");
        Console.WriteLine("  close-win                               Close the GUI");
        Console.WriteLine("  console                                 Open PowerShell in app directory");
        Console.WriteLine("  log [--last N] [--clear]                View CLI action history");
        Console.WriteLine("  terminal-list                           List active terminal sessions");
        Console.WriteLine("  terminal-send <grp> <tab> <text>        Send text to a terminal");
        Console.WriteLine("  terminal-key  <grp> <tab> <key>         Send a control key to a terminal");
        Console.WriteLine("  terminal-read <grp> <tab> [--last N]    Read terminal output text");
        Console.WriteLine("  bot-chat <message> [--from name]        Display external chat in AgentBot");
        Console.WriteLine("  help                                    Show detailed help");
    }

    private static int PrintUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.WriteLine();
        PrintUsage();
        return 1;
    }

    private static void AttachOrAllocConsole()
    {
        // Check if stdout is already valid (e.g. piped or redirected by parent shell).
        var existingHandle = NativeMethods.GetStdHandle(NativeMethods.STD_OUTPUT_HANDLE);
        bool hasStdOut = existingHandle != IntPtr.Zero && existingHandle != new IntPtr(-1);

        if (!hasStdOut)
        {
            // No valid stdout — attach to parent console or create new one.
            if (!NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS))
                NativeMethods.AllocConsole();
        }

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        Console.OutputEncoding = Encoding.UTF8;
    }
}
