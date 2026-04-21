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
                dwData = (IntPtr)0x4147,
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
        IntPtr agentWnd = NativeMethods.FindWindow(null, "AgentZero");
        if (agentWnd == IntPtr.Zero)
        {
            Console.Error.WriteLine("Error: AgentZero WPF is not running.");
            return 1;
        }

        SendWpfCommand(agentWnd, "{\"command\":\"status\"}");

        string? json = TryReadMmf("AgentZero_Status_Response", 8192);
        if (json == null) return _noWait ? 0 : 1;

        // Pretty-print the status
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        bool capturing = r.GetProperty("capturing").GetBoolean();
        string captureStatus = r.GetProperty("capture_status").GetString() ?? "";
        string statusBar = r.GetProperty("status_bar").GetString() ?? "";
        string hwnd = r.GetProperty("selected_hwnd").GetString() ?? "none";
        string winClass = r.GetProperty("window_class").GetString() ?? "";
        string winTitle = r.GetProperty("window_title").GetString() ?? "";
        string filterStart = r.GetProperty("filter_start").GetString() ?? "";
        string filterEnd = r.GetProperty("filter_end").GetString() ?? "";
        int scrollDelay = r.GetProperty("scroll_delay").GetInt32();
        int scrollMax = r.GetProperty("scroll_max").GetInt32();
        int scrollDelta = r.GetProperty("scroll_delta").GetInt32();
        int capturedLen = r.GetProperty("captured_length").GetInt32();

        Console.WriteLine("=== AgentZero WPF Status ===");
        Console.WriteLine();

        string stateIcon = capturing ? "[CAPTURING]" : "[IDLE]";
        Console.WriteLine($"  State:      {stateIcon}  {captureStatus}");
        Console.WriteLine($"  Status Bar: {statusBar}");
        Console.WriteLine();

        Console.WriteLine("  Target Window:");
        if (hwnd != "none")
        {
            Console.WriteLine($"    HWND:     {hwnd}");
            Console.WriteLine($"    Class:    {winClass}");
            Console.WriteLine($"    Title:    {(winTitle.Length > 70 ? winTitle[..70] + "..." : winTitle)}");
        }
        else
        {
            Console.WriteLine("    (none selected)");
        }
        Console.WriteLine();

        Console.WriteLine("  Date Filter:");
        Console.WriteLine($"    Start:    {(string.IsNullOrEmpty(filterStart) ? "(none)" : filterStart)}");
        Console.WriteLine($"    End:      {(string.IsNullOrEmpty(filterEnd) ? "(none)" : filterEnd)}");
        Console.WriteLine();

        Console.WriteLine($"  Scroll:     delay={scrollDelay}ms  max={scrollMax}  delta={scrollDelta}");
        Console.WriteLine($"  Captured:   {capturedLen:N0} chars");

        return 0;
    }

    // =========================================================================
    //  open-win / close-win
    // =========================================================================

    private static int CopyToClipboard()
    {
        IntPtr agentWnd = NativeMethods.FindWindow(null, "AgentZero");
        if (agentWnd == IntPtr.Zero)
        {
            Console.Error.WriteLine("Error: AgentZero WPF is not running.");
            return 1;
        }

        SendWpfCommand(agentWnd, "{\"command\":\"copy\"}");

        string? json = TryReadMmf("AgentZero_Copy_Response", 256);
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
        IntPtr existing = NativeMethods.FindWindow(null, "AgentZero");
        if (existing != IntPtr.Zero)
        {
            // Already running — bring to foreground
            NativeMethods.SetForegroundWindow(existing);
            Console.WriteLine("AgentZero WPF is already running. Brought to foreground.");
            return 0;
        }

        string exePath = Process.GetCurrentProcess().MainModule?.FileName
                         ?? Path.Combine(AppContext.BaseDirectory, "AgentZeroWpf.exe");

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
        IntPtr agentWnd = NativeMethods.FindWindow(null, "AgentZero");
        if (agentWnd == IntPtr.Zero)
        {
            Console.Error.WriteLine("Error: AgentZero WPF is not running.");
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

    private static IntPtr FindAgentZero()
    {
        IntPtr hwnd = NativeMethods.FindWindow(null, "AgentZero");
        if (hwnd == IntPtr.Zero)
        {
            Console.Error.WriteLine("Error: AgentZero is not running.");
            Console.Error.WriteLine("Start AgentZeroWpf.exe first (GUI mode), then retry.");
        }
        return hwnd;
    }

    // =========================================================================
    //  terminal-list : query active terminal sessions
    // =========================================================================

    private const string TerminalListMmfName = "AgentZero_TerminalList_Response";
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

    private const string TerminalSendMmfName = "AgentZero_TerminalSend_Response";
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

    private const string TerminalReadMmfName = "AgentZero_TerminalRead_Response";
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

    private const string BotChatMmfName = "AgentZero_BotChat_Response";
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

    private const string BotSignalMmfName = "AgentZero_BotSignal_Response";
    private const int BotSignalMmfSize = 1024;



    private static int PrintHelp()
    {
        PrintUsage();
        Console.WriteLine();
        Console.WriteLine("Command Details:");
        Console.WriteLine();
        Console.WriteLine("  get-window-info");
        Console.WriteLine("    Enumerates all visible windows and displays their properties.");
        Console.WriteLine("    Output: Handle, Class, Title, Rect, Process (PID), Style, ExStyle,");
        Console.WriteLine("            Visible, Enabled for each window.");
        Console.WriteLine();
        Console.WriteLine("  capture <hwnd> [--start YYYY-MM-DD] [--end YYYY-MM-DD]");
        Console.WriteLine("    Send capture command to the running AgentZero WPF instance.");
        Console.WriteLine("    <hwnd>  Target window handle (hex 0x... or decimal)");
        Console.WriteLine("    --start Start date for date-range filter (inclusive)");
        Console.WriteLine("    --end   End date for date-range filter (inclusive)");
        Console.WriteLine("    Scroll idle check: 5초간 새 텍스트 없으면 자동 종료");
        Console.WriteLine();
        Console.WriteLine("  status");
        Console.WriteLine("    Query the running WPF app's current state.");
        Console.WriteLine("    Shows: capture state (IDLE/CAPTURING), target window,");
        Console.WriteLine("    date filter, scroll settings, captured text length.");
        Console.WriteLine();
        Console.WriteLine("  copy");
        Console.WriteLine("    Copy captured text from WPF to system clipboard.");
        Console.WriteLine("    Returns the number of characters copied.");
        Console.WriteLine();
        Console.WriteLine("  open-win");
        Console.WriteLine("    Launch the WPF GUI. If already running, bring to foreground.");
        Console.WriteLine();
        Console.WriteLine("  close-win");
        Console.WriteLine("    Send close signal to the running WPF GUI.");
        Console.WriteLine();
        Console.WriteLine("  console");
        Console.WriteLine("    Open a new PowerShell window in the application directory.");
        Console.WriteLine();
        Console.WriteLine("  mousemove <x> <y>");
        Console.WriteLine("    Move the mouse cursor to screen coordinates (x, y).");
        Console.WriteLine();
        Console.WriteLine("  mouseclick <x> <y> [--right]");
        Console.WriteLine("    Click at screen coordinates. Default: left-click.");
        Console.WriteLine("    --right  Perform a right-click instead.");
        Console.WriteLine();
        Console.WriteLine("  mousewheel <x> <y> [--delta N]");
        Console.WriteLine("    Scroll the mouse wheel at screen coordinates.");
        Console.WriteLine("    --delta N  Wheel ticks (default: 3). Negative = scroll down.");
        Console.WriteLine();
        Console.WriteLine("  text-capture <hwnd> [--pick X Y]");
        Console.WriteLine("    Quick text capture from a window (no WPF GUI needed).");
        Console.WriteLine("    Extracts text via UIAutomation (TextPattern, TreeWalker).");
        Console.WriteLine("    Captured text goes to stdout; progress/status to stderr.");
        Console.WriteLine("    --pick X Y  Scroll target point (default: window center).");
        Console.WriteLine("    Output also saved to logs/scrap/ directory.");
        Console.WriteLine();
        Console.WriteLine("  scroll-capture <hwnd> [options]");
        Console.WriteLine("    Full scroll capture with date filtering (no WPF GUI needed).");
        Console.WriteLine("    Scrolls through content, collecting text via 4-stage fallback.");
        Console.WriteLine("    Supports Chromium (Chrome, Edge, Teams), KakaoTalk, and legacy apps.");
        Console.WriteLine("    Captured text goes to stdout; progress/status to stderr.");
        Console.WriteLine("    Options:");
        Console.WriteLine("      --delay N      Scroll interval in ms (default: 200, range: 50-5000)");
        Console.WriteLine("      --max N        Max scroll attempts (default: 500, range: 1-99999)");
        Console.WriteLine("      --delta N      Wheel multiplier (default: 3, range: 1-20)");
        Console.WriteLine("      --start DATE   Start date filter (YYYY-MM-DD). Stop if older.");
        Console.WriteLine("      --end DATE     End date filter (YYYY-MM-DD). Exclude newer.");
        Console.WriteLine("      --pick X Y     Scroll target point (default: window center).");
        Console.WriteLine("    Output also saved to logs/scrap/ directory.");
        Console.WriteLine("    Scroll idle: auto-stops after 5s with no new text.");
        Console.WriteLine("    Date pre-check: when --start is set, a quick initial capture runs");
        Console.WriteLine("      first to detect the latest content date. If all content is older");
        Console.WriteLine("      than --start, scroll is skipped immediately (fast exit).");
        Console.WriteLine();
        Console.WriteLine("  activate <hwnd>");
        Console.WriteLine("    Restore a minimized window and bring it to the foreground.");
        Console.WriteLine("    Works with minimized, background, or hidden windows.");
        Console.WriteLine("    After activation, reports the window's actual screen coordinates.");
        Console.WriteLine();
        Console.WriteLine("  keypress <text> [--delay N]");
        Console.WriteLine("    Type text as keyboard input (Unicode supported, including Korean).");
        Console.WriteLine("    --delay N  Delay between keystrokes in ms (default: 30).");
        Console.WriteLine();
        Console.WriteLine("  keypress --key <keyname|combo>");
        Console.WriteLine("    Press a special key or key combination.");
        Console.WriteLine("    Keys: tab, enter, esc, space, backspace, delete, insert,");
        Console.WriteLine("          home, end, pageup, pagedown, up, down, left, right,");
        Console.WriteLine("          f1-f12, capslock, pause, win");
        Console.WriteLine("    Combos: ctrl+c, ctrl+shift+a, alt+f4, ctrl+v, etc.");
        Console.WriteLine();
        Console.WriteLine("  element-tree <hwnd> [--search keyword] [--depth N]");
        Console.WriteLine("    Scan UI Automation accessibility tree of a window.");
        Console.WriteLine("    Detects Flutter, Electron/Chromium, or Native framework.");
        Console.WriteLine("    --search keyword  Filter nodes containing the keyword.");
        Console.WriteLine("    --depth N         Max tree depth (default: 30, max: 100).");
        Console.WriteLine("    Output: indented tree to stdout, metadata to stderr.");
        Console.WriteLine();
        Console.WriteLine("  log [--last N] [--clear]");
        Console.WriteLine("    View CLI action history (mouseclick, keypress, activate, screenshot).");
        Console.WriteLine("    Each action records timestamp, coordinates, and reaction (title change).");
        Console.WriteLine("    --last N   Show last N entries (default: 50, max: 500).");
        Console.WriteLine("    --clear    Trim old entries (keep last 500).");
        Console.WriteLine("    Tip: Use this to review what actions were performed and whether");
        Console.WriteLine("         clicks had the expected effect (window title changes).");
        Console.WriteLine("    Log file: logs/cli-actions.log");
        Console.WriteLine();
        Console.WriteLine("  screenshot [--color] [--original] [--at X Y] [--size N]");
        Console.WriteLine("    Capture a desktop screenshot and save as PNG.");
        Console.WriteLine("    Default: grayscale, resized to 1920x1080 (low-res for AI analysis).");
        Console.WriteLine("    --color     Save in color (still resized to 1920x1080).");
        Console.WriteLine("    --original  Save at native screen resolution in color (no resize).");
        Console.WriteLine("    --at X Y    Crop region centered at physical (X,Y). Color, 1:1 pixels.");
        Console.WriteLine("    --size N    Region size in pixels (default: 300, range: 50-1000).");
        Console.WriteLine("    Output: screenshot/YYYYMMDDHHmmss-desktop.png (full)");
        Console.WriteLine("            screenshot/YYYYMMDDHHmmss-region.png  (--at)");
        Console.WriteLine();
        Console.WriteLine("    Use cases:");
        Console.WriteLine("      - Full screenshot: broad UI overview, identify rough positions");
        Console.WriteLine("      - Region screenshot (--at): precision targeting of UI components");
        Console.WriteLine("        Use after a full screenshot to zoom into a specific area");
        Console.WriteLine("      - App window coordinates can be obtained via get-window-info/wininfo-layout");
        Console.WriteLine("        without a screenshot, but identifying components WITHIN an app");
        Console.WriteLine("        (buttons, text fields, menus) requires a screenshot for visual analysis");
        Console.WriteLine();
        Console.WriteLine("  wininfo-layout <hwnd>");
        Console.WriteLine("    Analyze a window as 2-panel layout (3:7 ratio).");
        Console.WriteLine("    Returns left/right panel rects and center mouse coordinates.");
        Console.WriteLine("    Left panel (30%): channel/chat list, navigation area.");
        Console.WriteLine("    Right panel (70%): detail/content area.");
        Console.WriteLine("    Output includes both human-readable and JSON format.");
        Console.WriteLine();
        Console.WriteLine("  terminal-list");
        Console.WriteLine("    Query active terminal sessions from AgentZero.");
        Console.WriteLine("    Returns group/tab indices, titles, session IDs, and running state.");
        Console.WriteLine("    Use the group_index and tab_index with terminal-send.");
        Console.WriteLine();
        Console.WriteLine("  terminal-send <group_index> <tab_index> <text...>");
        Console.WriteLine("    Send text + Enter to a specific terminal session.");
        Console.WriteLine("    Use 'terminal-list' first to discover available sessions.");
        Console.WriteLine("    Example: terminal-send 0 0 ls -la");
        Console.WriteLine();
        Console.WriteLine("  terminal-read <group_index> <tab_index> [--last N]");
        Console.WriteLine("    Read console output text from the specified terminal.");
        Console.WriteLine("    ANSI escape codes are stripped for clean text output.");
        Console.WriteLine("    --last N  Return only the last N characters (default: all visible text)");
        Console.WriteLine("    Example: terminal-read 0 0 --last 2000");
        Console.WriteLine();
        Console.WriteLine("  bot-chat <message...> [--from <name>]");
        Console.WriteLine("    Send a chat message to the AgentBot window (UI render only, no AI trigger).");
        Console.WriteLine("    --from <name>  Sender name displayed in chat (default: \"CLI\")");
        Console.WriteLine("    Example: bot-chat Hello from automation --from MyScript");
        Console.WriteLine();
        Console.WriteLine("  bot-signal <kind> [--from <name>] [--to <name>] [--message <text...>]");
        Console.WriteLine("    Send a structured peer signal to the AgentBot broker.");
        Console.WriteLine("    Recommended for done/status/error/partial and peer relay requests.");
        Console.WriteLine("    Example: bot-signal done --from Claude1 --message \"작업 완료\"");
        Console.WriteLine("    Example: bot-signal relay --from ClaudeA --to ClaudeB --message \"이 설계를 검토해줘\"");
        Console.WriteLine();
        Console.WriteLine("  tell-ai <message...> [--from <name>]");
        Console.WriteLine("    Send a user prompt to the AgentBot AI and trigger its LLM response.");
        Console.WriteLine("    Fire-and-forget: ACK returned immediately; the answer streams in AgentBot.");
        Console.WriteLine("    --from <name>  Sender name (default: \"CLI\")");
        Console.WriteLine("    Safety: External AI trigger is disallowed by default. Enable in Settings.");
        Console.WriteLine("    Example: tell-ai \"Review the Phase 1 plan\" --from Claude");
        Console.WriteLine();
        Console.WriteLine("  help, --help, -h, /?");
        Console.WriteLine("    Show this help message.");
        Console.WriteLine();
        Console.WriteLine("Global Options:");
        Console.WriteLine();
        Console.WriteLine("  --no-wait");
        Console.WriteLine("    Fire-and-forget mode: send WM_COPYDATA command to WPF and exit");
        Console.WriteLine("    immediately without waiting for MMF response.");
        Console.WriteLine("    Use with: status, copy (commands that communicate with WPF).");
        Console.WriteLine("    Exit code: 0 if command sent, 1 if WPF not running.");
        Console.WriteLine();
        Console.WriteLine("  --timeout N");
        Console.WriteLine("    Wait up to N milliseconds for WPF response (default: 5000).");
        Console.WriteLine("    CLI polls MMF every 300ms until response or timeout.");
        Console.WriteLine("    Mutually exclusive with --no-wait (--no-wait takes precedence).");
        Console.WriteLine();
        Console.WriteLine("  IPC Pipeline: CLI → WM_COPYDATA → WPF (process) → MMF response → CLI");
        Console.WriteLine("    Commands like status/copy send JSON via WM_COPYDATA to WPF,");
        Console.WriteLine("    then poll a Memory-Mapped File for the response.");
        Console.WriteLine("    --no-wait skips the polling step (fire-and-forget).");
        Console.WriteLine("    --timeout adjusts how long to poll before giving up.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  AgentZeroWpf.exe -cli terminal-list");
        Console.WriteLine("  AgentZeroWpf.exe -cli terminal-send 0 0 ls -la");
        Console.WriteLine("  AgentZeroWpf.exe -cli terminal-send 0 1 git status");
        Console.WriteLine("  AgentZeroWpf.exe -cli terminal-read 0 0");
        Console.WriteLine("  AgentZeroWpf.exe -cli terminal-read 0 0 --last 2000");
        Console.WriteLine("  AgentZeroWpf.exe -cli bot-chat Hello from automation");
        Console.WriteLine("  AgentZeroWpf.exe -cli bot-chat Build finished --from CI");
        Console.WriteLine("  AgentZeroWpf.exe -cli bot-signal done --from Claude1 --message 작업완료");
        Console.WriteLine("  AgentZeroWpf.exe -cli bot-signal relay --from ClaudeA --to ClaudeB --message 설계검토");
        Console.WriteLine("  AgentZeroWpf.exe -cli get-window-info");
        Console.WriteLine("  AgentZeroWpf.exe -cli open-win");
        Console.WriteLine("  AgentZeroWpf.exe -cli capture 0x000C024C --start 2026-03-01 --end 2026-03-21");
        Console.WriteLine("  AgentZeroWpf.exe -cli status");
        Console.WriteLine("  AgentZeroWpf.exe -cli status --no-wait");
        Console.WriteLine("  AgentZeroWpf.exe -cli copy --no-wait");
        Console.WriteLine("  AgentZeroWpf.exe -cli copy --timeout 10000");
        Console.WriteLine("  AgentZeroWpf.exe -cli mousemove 500 300");
        Console.WriteLine("  AgentZeroWpf.exe -cli mouseclick 500 300 --right");
        Console.WriteLine("  AgentZeroWpf.exe -cli mousewheel 500 300 --delta -5");
        Console.WriteLine("  AgentZeroWpf.exe -cli keypress Hello World");
        Console.WriteLine("  AgentZeroWpf.exe -cli keypress --key tab");
        Console.WriteLine("  AgentZeroWpf.exe -cli keypress --key ctrl+a");
        Console.WriteLine("  AgentZeroWpf.exe -cli keypress --key alt+f4");
        Console.WriteLine("  AgentZeroWpf.exe -cli text-capture 0x000C024C");
        Console.WriteLine("  AgentZeroWpf.exe -cli scroll-capture 0x000C024C --start 2026-03-01 --end 2026-04-01");
        Console.WriteLine("  AgentZeroWpf.exe -cli scroll-capture 0x000C024C --max 9999");
        Console.WriteLine("  AgentZeroWpf.exe -cli scroll-capture 0x000C024C --pick 500 400 --delta 5");
        Console.WriteLine("  AgentZeroWpf.exe -cli screenshot");
        Console.WriteLine("  AgentZeroWpf.exe -cli screenshot --color");
        Console.WriteLine("  AgentZeroWpf.exe -cli screenshot --original");
        Console.WriteLine("  AgentZeroWpf.exe -cli screenshot --at 200 300");
        Console.WriteLine("  AgentZeroWpf.exe -cli screenshot --at 200 300 --size 500");
        Console.WriteLine("  AgentZeroWpf.exe -cli wininfo-layout 0x000C024C");
        Console.WriteLine("  AgentZeroWpf.exe -cli close-win");
        Console.WriteLine();
        Console.WriteLine("=== Windowless Capture Workflow (no WPF GUI) ===");
        Console.WriteLine();
        Console.WriteLine("  Quick text grab:");
        Console.WriteLine("    -cli get-window-info                    # find target HWND");
        Console.WriteLine("    -cli text-capture <hwnd>                # capture text → stdout");
        Console.WriteLine("    -cli text-capture <hwnd> > output.txt   # save to file");
        Console.WriteLine();
        Console.WriteLine("  Full scroll capture with date filter:");
        Console.WriteLine("    -cli get-window-info                    # find HWND");
        Console.WriteLine("    -cli activate <hwnd>                    # restore if minimized");
        Console.WriteLine("    -cli scroll-capture <hwnd> --start 2026-03-01 > chat.txt");
        Console.WriteLine();
        Console.WriteLine("  Infinite scroll capture (max attempts):");
        Console.WriteLine("    -cli scroll-capture <hwnd> --max 9999   # stops on idle (5s no new text)");
        Console.WriteLine();
        Console.WriteLine("  Targeted scroll (specific area via screenshot):");
        Console.WriteLine("    -cli screenshot --at <x> <y>            # find scroll area coords");
        Console.WriteLine("    -cli scroll-capture <hwnd> --pick <x> <y> --start 2026-01-01");
        Console.WriteLine();
        Console.WriteLine("=== Scroll Capture Workflow (with WPF GUI) ===");
        Console.WriteLine();
        Console.WriteLine("  Step 1. Find the target window handle:");
        Console.WriteLine("          -cli get-window-info");
        Console.WriteLine();
        Console.WriteLine("  Step 2. Get panel layout & mouse positions:");
        Console.WriteLine("          -cli wininfo-layout <hwnd>");
        Console.WriteLine("          Left panel center  = channel/chat list scroll area");
        Console.WriteLine("          Right panel center = detail/content scroll area");
        Console.WriteLine();
        Console.WriteLine("  Step 3. Start AgentZero WPF (if not running):");
        Console.WriteLine("          -cli open-win");
        Console.WriteLine();
        Console.WriteLine("  Step 4. Send capture command (starts after 3 sec countdown):");
        Console.WriteLine("          -cli capture <hwnd> [--start YYYY-MM-DD] [--end YYYY-MM-DD]");
        Console.WriteLine();
        Console.WriteLine("  Step 5. Move mouse to the scroll target area before capture begins:");
        Console.WriteLine("          -cli mousemove <center_x> <center_y>");
        Console.WriteLine("          (Use the center coordinates from Step 2)");
        Console.WriteLine();
        Console.WriteLine("  Step 6. Focus the target window & check capture progress:");
        Console.WriteLine("          -cli mouseclick <center_x> <center_y>");
        Console.WriteLine("          (Click on the target area to ensure window has focus for scrolling)");
        Console.WriteLine();
        Console.WriteLine("  Step 7. Check capture progress/completion:");
        Console.WriteLine("          -cli status");
        Console.WriteLine("          [CAPTURING] = in progress, [IDLE] = done");
        Console.WriteLine();
        Console.WriteLine("  Step 8. Copy captured text to clipboard:");
        Console.WriteLine("          -cli copy");
        Console.WriteLine();
        Console.WriteLine("  Step 9. Use clipboard data as needed (paste, process, etc.)");
        Console.WriteLine();
        Console.WriteLine("  Step 10. Close AgentZero WPF when done:");
        Console.WriteLine("          -cli close-win");
        Console.WriteLine();
        Console.WriteLine("=== Screenshot for AI Coordinate Estimation ===");
        Console.WriteLine();
        Console.WriteLine("  Coordinate system: physical pixels (same as SetCursorPos).");
        Console.WriteLine("  Screen size from GetSystemMetrics = mouse coordinate space.");
        Console.WriteLine();
        Console.WriteLine("  --- Full screenshot workflow ---");
        Console.WriteLine();
        Console.WriteLine("  Step 1. Take a full screenshot for overview:");
        Console.WriteLine("          -cli screenshot");
        Console.WriteLine("          Output shows: mouse_x = image_x * screenW / 1920");
        Console.WriteLine();
        Console.WriteLine("  Step 2. Identify approximate target area from full screenshot.");
        Console.WriteLine("          Use the mapping formula to convert to mouse coordinates.");
        Console.WriteLine();
        Console.WriteLine("  --- Region screenshot workflow (precision targeting) ---");
        Console.WriteLine();
        Console.WriteLine("  Step 3. Crop a region around the target for precision:");
        Console.WriteLine("          -cli screenshot --at <x> <y> [--size 300]");
        Console.WriteLine("          Image pixels map 1:1 to physical screen pixels.");
        Console.WriteLine("          mouse_coord = origin + image_pixel");
        Console.WriteLine();
        Console.WriteLine("  Step 4. AI estimates pixel position of the target in the region image.");
        Console.WriteLine();
        Console.WriteLine("  *** DPI CORRECTION (IMPORTANT) ***");
        Console.WriteLine("  AI image viewers typically downscale high-DPI images for display.");
        Console.WriteLine("  When DPI > 100%, AI's visual pixel estimates are SMALLER than actual.");
        Console.WriteLine("  Apply DPI correction before using the mapping formula:");
        Console.WriteLine();
        Console.WriteLine("    corrected_pixel = AI_estimated_pixel * (DPI / 96)");
        Console.WriteLine("    mouse_coord     = origin + corrected_pixel");
        Console.WriteLine();
        Console.WriteLine("  Example (DPI=144, 150%):");
        Console.WriteLine("    AI estimates button center at pixel (100, 80) in region image");
        Console.WriteLine("    corrected = (100 * 1.50, 80 * 1.50) = (150, 120)");
        Console.WriteLine("    If origin = (50, 200): mouse = (200, 320)");
        Console.WriteLine();
        Console.WriteLine("  Step 5. Click the corrected coordinates:");
        Console.WriteLine("          -cli mouseclick <mouse_x> <mouse_y>");
        Console.WriteLine();
        Console.WriteLine("  Step 6. Verify with another region screenshot:");
        Console.WriteLine("          -cli screenshot --at <mouse_x> <mouse_y>");
        Console.WriteLine();
        Console.WriteLine("  Tip: For app-level coordinates (window position/size), use");
        Console.WriteLine("       get-window-info or wininfo-layout — no screenshot needed.");
        Console.WriteLine("       Screenshots are for identifying components WITHIN a window.");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("AgentZero CLI Mode");
        Console.WriteLine();
        Console.WriteLine("Usage: AgentZeroWpf.exe -cli <command> [--no-wait] [--timeout N]");
        Console.WriteLine();
        Console.WriteLine("Global Options:");
        Console.WriteLine("  --no-wait              Fire-and-forget: send command, skip response");
        Console.WriteLine("  --timeout N            Wait up to N ms for response (default: 5000)");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  get-window-info                       List all visible windows");
        Console.WriteLine("  capture <hwnd> [--start] [--end]      Send capture to running WPF");
        Console.WriteLine("  status                                Show WPF app state");
        Console.WriteLine("  copy                                  Copy captured text to clipboard");
        Console.WriteLine("  open-win                              Launch WPF GUI");
        Console.WriteLine("  close-win                             Close WPF GUI");
        Console.WriteLine("  console                               Open PowerShell in app directory");
        Console.WriteLine("  mousemove <x> <y>                     Move mouse cursor");
        Console.WriteLine("  mouseclick <x> <y> [--right]          Click at position");
        Console.WriteLine("  mousewheel <x> <y> [--delta N]        Scroll wheel at position");
        Console.WriteLine("  activate <hwnd>                       Restore & focus window");
        Console.WriteLine("  keypress <text> | --key <combo>       Type text or press keys");
        Console.WriteLine("  text-capture <hwnd>                    Quick text capture (stdout)");
        Console.WriteLine("  scroll-capture <hwnd> [options]        Scroll capture with filters");
        Console.WriteLine("  log [--last N] [--clear]               View action history");
        Console.WriteLine("  dpi                                    Show DPI & coordinate mapping");
        Console.WriteLine("  screenshot [options]                   Desktop/region screenshot");
        Console.WriteLine("  element-tree <hwnd> [options]          UI Automation element tree");
        Console.WriteLine("  wininfo-layout <hwnd>                 2-panel layout (3:7)");
        Console.WriteLine("  terminal-list                         List active terminal sessions");
        Console.WriteLine("  terminal-send <grp> <tab> <text>      Send text to terminal");
        Console.WriteLine("  terminal-read <grp> <tab> [--last N]  Read terminal output text");
        Console.WriteLine("  bot-chat <message> [--from name]      Send chat to AgentBot (UI only)");
        Console.WriteLine("  bot-signal <kind> ...                 Send structured peer signal to broker");
        Console.WriteLine("  tell-ai <message> [--from name]       Send prompt to AgentBot AI (triggers LLM)");
        Console.WriteLine("  help                                  Show detailed help");
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
