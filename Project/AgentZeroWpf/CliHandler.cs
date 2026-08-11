using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Agent.Common.Module;
using AgentZeroWpf.Module;
using AgentZeroWpf.OsControl;
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
            "help" or "--help" or "-h" or "/?" => Help(cliArgs.Skip(1).ToArray()),
            "version" or "--version" or "-v" => PrintVersion(),
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
            "agent-hook" => AgentHook(cliArgs.Skip(1).ToArray()),
            "agent-hook-install" => AgentHookInstall(),
            "agent-hook-uninstall" => AgentHookUninstall(),
            "trust-workspace" => TrustWorkspace(cliArgs.Skip(1).ToArray()),
            "cost" => ShowCost(),
            "worktree" => Worktree(cliArgs.Skip(1).ToArray()),
            "terminal-wait" => TerminalWait(cliArgs.Skip(1).ToArray()),
            "skill-stub-install" => SkillStubInstall(),
            "skill-stub-uninstall" => SkillStubUninstall(),
            "orchestrate" => Orchestrate(cliArgs.Skip(1).ToArray()),
            "automation" => Automation(cliArgs.Skip(1).ToArray()),
            "os" => OsCliCommands.Dispatch(cliArgs.Skip(1).ToArray()),
            _ => PrintUnknownCommand(command),
        };
    }


    // Bound the WM_COPYDATA send so a busy/hung WPF UI thread can't infinitely
    // block the CLI. SendMessageW (no-timeout) was the recurring source of
    // "CLI 블락 현상" — see harness/logs/code-coach/2026-05-10-07-51-cli-block-recurrence-rca.md.
    private const uint WpfSendTimeoutMs = 3000;

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
            var rc = NativeMethods.SendMessageTimeoutCopyData(
                agentWnd, NativeMethods.WM_COPYDATA, IntPtr.Zero, ref cds,
                NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_NORMAL,
                WpfSendTimeoutMs,
                out _);
            if (rc == IntPtr.Zero)
            {
                Console.Error.WriteLine(
                    $"Error: AgentZero GUI unresponsive (WM_COPYDATA timed out after {WpfSendTimeoutMs}ms). " +
                    "The GUI is alive but its UI thread is blocked. Check the log panel for a stuck operation, then retry.");
                return false;
            }
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

        if (!SendWpfCommand(agentWnd, "{\"command\":\"status\"}"))
            return 1;

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

        if (!SendWpfCommand(agentWnd, "{\"command\":\"copy\"}"))
            return 1;

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

        string exePath = GetSelfExePath();

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

        if (!SendWpfCommand(agentWnd, "{\"command\":\"terminal-list\"}"))
            return 1;

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

        if (!SendWpfCommand(agentWnd, sb.ToString()))
            return 1;

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

        if (!SendWpfCommand(agentWnd, sb.ToString()))
            return 1;

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

        if (!SendWpfCommand(agentWnd, sb.ToString()))
            return 1;

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

    /// <summary>Reads a terminal's recent output text over IPC (helper for terminal-wait).</summary>
    private static string? ReadTerminalTextRaw(IntPtr agentWnd, int groupIdx, int tabIdx, int lastN)
    {
        var sb = new StringBuilder();
        sb.Append("{\"command\":\"terminal-read\"");
        sb.Append($",\"group_index\":{groupIdx}");
        sb.Append($",\"tab_index\":{tabIdx}");
        sb.Append($",\"last\":{lastN}");
        sb.Append('}');
        if (!SendWpfCommand(agentWnd, sb.ToString())) return null;
        var json = TryReadMmf(TerminalReadMmfName, TerminalReadMmfSize);
        if (json == null) return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return root.TryGetProperty("ok", out var ok) && ok.GetBoolean()
            ? root.GetProperty("text").GetString() ?? ""
            : null;
    }

    // =========================================================================
    //  terminal-wait <grp> <tab> [--timeout-ms N] [--idle-ms N]  (mission W4)
    //      : block until a terminal's output stops changing (TUI-idle), so an
    //      agent can wait for a peer instead of polling read in a loop.
    // =========================================================================
    private static int TerminalWait(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[0], out int grp) || !int.TryParse(args[1], out int tab))
        {
            Console.Error.WriteLine("Usage: terminal-wait <group> <tab> [--timeout-ms N] [--idle-ms N]");
            return 1;
        }
        int timeoutMs = 60000, idleMs = 1500, pollMs = 400;
        for (int i = 2; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--timeout-ms", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var t)) timeoutMs = t;
            else if (args[i].Equals("--idle-ms", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var d)) idleMs = d;
        }

        IntPtr agentWnd = FindAgentZero();
        if (agentWnd == IntPtr.Zero) return 1;

        var total = Stopwatch.StartNew();
        var idle = Stopwatch.StartNew();
        string last = ReadTerminalTextRaw(agentWnd, grp, tab, 2000) ?? "";
        while (total.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(pollMs);
            var cur = ReadTerminalTextRaw(agentWnd, grp, tab, 2000) ?? last;
            if (!string.Equals(cur, last, StringComparison.Ordinal))
            {
                last = cur;
                idle.Restart();
            }
            else if (idle.ElapsedMilliseconds >= idleMs)
            {
                Console.WriteLine($"idle (stable {idleMs}ms) after {total.ElapsedMilliseconds}ms");
                return 0;
            }
        }
        Console.Error.WriteLine($"terminal-wait: timed out after {timeoutMs}ms without going idle");
        return 2;
    }

    // =========================================================================
    //  orchestrate <list | create <file.json> | status <runId>>  (mission W6)
    //      : manage supervised multi-agent runs in the local DB (in-process).
    //      create JSON: { "name": "...", "tasks": [ { "key","prompt","deps":[] } ] }
    //      NOTE: actual execution (dispatching to live agents) runs inside the
    //      GUI coordinator — this CLI covers durable create/inspect. (follow-up)
    // =========================================================================
    private static int Orchestrate(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: orchestrate <list | create <file.json> | status <runId>>");
            return 1;
        }
        try
        {
            EnsureDbReady();
            using var db = new Agent.Common.Data.AppDbContext();
            var sub = args[0].ToLowerInvariant();
            switch (sub)
            {
                case "list":
                {
                    var runs = db.OrchestrationRuns.OrderByDescending(r => r.Id).Take(20).ToList();
                    if (runs.Count == 0) { Console.WriteLine("No orchestration runs."); return 0; }
                    foreach (var r in runs)
                        Console.WriteLine($"  #{r.Id,-4} {r.Status,-9} {r.Name}");
                    return 0;
                }
                case "create":
                {
                    if (args.Length < 2) { Console.Error.WriteLine("Usage: orchestrate create <file.json>"); return 1; }
                    if (!File.Exists(args[1])) { Console.Error.WriteLine($"File not found: {args[1]}"); return 1; }
                    using var doc = JsonDocument.Parse(File.ReadAllText(args[1]));
                    var root = doc.RootElement;
                    var name = root.TryGetProperty("name", out var np) ? np.GetString() ?? "run" : "run";
                    var specs = new List<Agent.Common.Actors.OrchestrationTaskSpec>();
                    foreach (var t in root.GetProperty("tasks").EnumerateArray())
                    {
                        var key = t.GetProperty("key").GetString() ?? "";
                        var prompt = t.TryGetProperty("prompt", out var pp) ? pp.GetString() ?? "" : "";
                        var deps = t.TryGetProperty("deps", out var dp) && dp.ValueKind == JsonValueKind.Array
                            ? dp.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
                            : new List<string>();
                        specs.Add(new Agent.Common.Actors.OrchestrationTaskSpec(key, prompt, deps));
                    }
                    var runId = Agent.Common.Orchestration.OrchestrationStore.CreateRun(db, name, specs);
                    Console.WriteLine($"Created run #{runId} '{name}' with {specs.Count} task(s).");
                    return 0;
                }
                case "run":
                {
                    if (args.Length < 2 || !int.TryParse(args[1], out var runId)) { Console.Error.WriteLine("Usage: orchestrate run <runId>"); return 1; }
                    // Execution needs the live actor system + terminals → hand off to the GUI.
                    IntPtr wnd = FindAgentZero();
                    if (wnd == IntPtr.Zero) return 1;
                    var sb = new StringBuilder();
                    sb.Append("{\"command\":\"orchestrate-run\"");
                    sb.Append($",\"run_id\":{runId}");
                    sb.Append('}');
                    if (!SendWpfCommand(wnd, sb.ToString())) return 1;
                    Console.WriteLine($"Requested run #{runId} start. Track with: orchestrate status {runId}");
                    return 0;
                }
                case "status":
                {
                    if (args.Length < 2 || !int.TryParse(args[1], out var runId)) { Console.Error.WriteLine("Usage: orchestrate status <runId>"); return 1; }
                    var run = db.OrchestrationRuns.FirstOrDefault(r => r.Id == runId);
                    if (run is null) { Console.Error.WriteLine($"Run #{runId} not found."); return 1; }
                    Console.WriteLine($"Run #{run.Id} '{run.Name}' — {run.Status}");
                    foreach (var t in db.OrchestrationTasks.Where(t => t.RunId == runId).OrderBy(t => t.Id))
                    {
                        var deps = Agent.Common.Orchestration.OrchestrationMapper.ParseDeps(t.DependsOnJson);
                        var depStr = deps.Count > 0 ? $" ← [{string.Join(",", deps)}]" : "";
                        Console.WriteLine($"  {t.TaskKey,-12} {t.Status,-10}{depStr}");
                    }
                    return 0;
                }
                default:
                    Console.Error.WriteLine($"Unknown orchestrate subcommand: {sub}");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    // =========================================================================
    //  automation <create|list|remove|due> ...
    //      : scheduled agent runs. create computes the next fire time; the GUI
    //      scheduler dispatches due automations to the bot. In-process DB.
    //      create --name X --schedule "every 30m|hourly|daily HH:mm" --prompt "..." [--workspace path]
    // =========================================================================
    private static int Automation(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: automation <create|list|remove <id>|due>");
            Console.Error.WriteLine("  create --name X --schedule \"every 30m|hourly|daily HH:mm\" --prompt \"...\" [--workspace path]");
            return 1;
        }
        try
        {
            EnsureDbReady();
            using var db = new Agent.Common.Data.AppDbContext();
            var sub = args[0].ToLowerInvariant();
            switch (sub)
            {
                case "list":
                {
                    var all = db.Automations.OrderBy(a => a.Id).ToList();
                    if (all.Count == 0) { Console.WriteLine("No automations."); return 0; }
                    foreach (var a in all)
                        Console.WriteLine($"  #{a.Id,-3} {(a.Enabled ? "on " : "off")} [{a.Schedule,-14}] next={a.NextRunUtc:u}  {a.Name}");
                    return 0;
                }
                case "due":
                {
                    var nowUtc = DateTime.UtcNow;
                    var due = db.Automations.Where(a => a.Enabled && a.NextRunUtc != null && a.NextRunUtc <= nowUtc).ToList();
                    Console.WriteLine(due.Count == 0 ? "Nothing due." : $"{due.Count} due now:");
                    foreach (var a in due) Console.WriteLine($"  #{a.Id} {a.Name}");
                    return 0;
                }
                case "remove":
                {
                    if (args.Length < 2 || !int.TryParse(args[1], out var id)) { Console.Error.WriteLine("Usage: automation remove <id>"); return 1; }
                    var a = db.Automations.Find(id);
                    if (a is null) { Console.Error.WriteLine($"#{id} not found."); return 1; }
                    db.Automations.Remove(a); db.SaveChanges();
                    Console.WriteLine($"Removed automation #{id}.");
                    return 0;
                }
                case "create":
                {
                    string name = "", schedule = "", prompt = "", workspace = "";
                    for (int i = 1; i < args.Length; i++)
                    {
                        switch (args[i].ToLowerInvariant())
                        {
                            case "--name" when i + 1 < args.Length: name = args[++i]; break;
                            case "--schedule" when i + 1 < args.Length: schedule = args[++i]; break;
                            case "--prompt" when i + 1 < args.Length: prompt = args[++i]; break;
                            case "--workspace" when i + 1 < args.Length: workspace = args[++i]; break;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(schedule) || string.IsNullOrWhiteSpace(prompt))
                    {
                        Console.Error.WriteLine("create requires --schedule and --prompt");
                        return 1;
                    }
                    if (!Agent.Common.Automations.AutomationSchedule.TryComputeNext(schedule, DateTime.UtcNow, out var next, out var err))
                    {
                        Console.Error.WriteLine($"Invalid schedule: {err}");
                        return 1;
                    }
                    var auto = new Agent.Common.Data.Entities.Automation
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? "automation" : name,
                        Schedule = schedule, Prompt = prompt, WorkspacePath = workspace,
                        Enabled = true, NextRunUtc = next, CreatedAtUtc = DateTime.UtcNow,
                    };
                    db.Automations.Add(auto); db.SaveChanges();
                    Console.WriteLine($"Created automation #{auto.Id} '{auto.Name}' — next run {next:u}");
                    return 0;
                }
                default:
                    Console.Error.WriteLine($"Unknown automation subcommand: {sub}");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    // =========================================================================
    //  skill-stub-install / skill-stub-uninstall  (mission W5)
    //      : write the anti-drift AgentZero skill STUB into each ~/.claude*
    //      skills folder. Explicit/consented; the full guide stays served by
    //      `-cli help agentzero`.
    // =========================================================================
    private static int SkillStubInstall()
    {
        var results = AgentZeroWpf.Services.SkillStubInjector.InstallAll();
        if (results.Count == 0) { Console.WriteLine("No Claude Code profiles (~/.claude*) found."); return 0; }
        int failed = 0;
        foreach (var r in results)
        {
            if (r.Ok) Console.WriteLine($"  [{r.AccountKey}] {r.Action}");
            else { Console.Error.WriteLine($"  [{r.AccountKey}] FAILED: {r.Error}"); failed++; }
        }
        Console.WriteLine(failed == 0 ? "Skill stub installed (full guide via `-cli help agentzero`)." : $"Completed with {failed} failure(s).");
        return failed == 0 ? 0 : 1;
    }

    private static int SkillStubUninstall()
    {
        var results = AgentZeroWpf.Services.SkillStubInjector.UninstallAll();
        if (results.Count == 0) { Console.WriteLine("No Claude Code profiles (~/.claude*) found."); return 0; }
        foreach (var r in results)
            Console.WriteLine(r.Ok ? $"  [{r.AccountKey}] {r.Action}" : $"  [{r.AccountKey}] FAILED: {r.Error}");
        return 0;
    }

    // =========================================================================
    //  worktree <list|add|remove> ...  (missions W4/W7)
    //      : git worktree management in the current directory's repo. In-process.
    // =========================================================================
    private static int Worktree(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: worktree <list | add <path> [branch] | remove <path> [--force]>");
            return 1;
        }
        var cwd = Directory.GetCurrentDirectory();
        var sub = args[0].ToLowerInvariant();
        switch (sub)
        {
            case "list":
            {
                var list = System.Threading.Tasks.Task.Run(() => Agent.Common.Module.GitWorktreeBuilder.ListAsync(cwd)).GetAwaiter().GetResult();
                if (list.Count == 0) { Console.WriteLine("No worktrees (or not a git repo)."); return 0; }
                foreach (var w in list)
                {
                    var label = w.Detached ? "detached" : (string.IsNullOrEmpty(w.Branch) ? "?" : w.Branch);
                    var head = w.Head.Length >= 7 ? w.Head[..7] : w.Head;
                    Console.WriteLine($"  {w.Path}  [{label}]  {head}");
                }
                return 0;
            }
            case "add":
            {
                if (args.Length < 2) { Console.Error.WriteLine("Usage: worktree add <path> [branch] [--trust]"); return 1; }
                var branch = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : null;
                var res = System.Threading.Tasks.Task.Run(() => Agent.Common.Module.GitWorktreeBuilder.AddAsync(cwd, args[1], branch)).GetAwaiter().GetResult();
                if (!res.Ok) { Console.Error.WriteLine(res.StdErr.Trim()); return 1; }
                Console.WriteLine($"Added worktree: {args[1]}" + (branch is null ? " (detached)" : $" (branch {branch})"));

                // W7↔W2 integration: optionally pre-trust the new worktree so a
                // hosted agent CLI can launch in it without a trust prompt.
                if (args.Contains("--trust"))
                {
                    var abs = Path.GetFullPath(Path.IsPathRooted(args[1]) ? args[1] : Path.Combine(cwd, args[1]));
                    foreach (var t in Agent.Common.Agents.TrustPresetWriter.MarkAllTrusted(abs))
                        Console.WriteLine($"    trust[{t.Agent}]: {(t.Ok ? t.Detail : "FAILED " + t.Detail)}");
                }
                return 0;
            }
            case "remove":
            {
                if (args.Length < 2) { Console.Error.WriteLine("Usage: worktree remove <path> [--force]"); return 1; }
                bool force = args.Contains("--force");
                var res = System.Threading.Tasks.Task.Run(() => Agent.Common.Module.GitWorktreeBuilder.RemoveAsync(cwd, args[1], force)).GetAwaiter().GetResult();
                if (res.Ok) { Console.WriteLine($"Removed worktree: {args[1]}"); return 0; }
                Console.Error.WriteLine(res.StdErr.Trim()); return 1;
            }
            default:
                Console.Error.WriteLine($"Unknown worktree subcommand: {sub}");
                return 1;
        }
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

        if (!SendWpfCommand(agentWnd, sb.ToString()))
            return 1;

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
    //  agent-hook --event <name> [--state <phase>] [--session <id>] [--detail <t>]
    //      : fire-and-forget agent-CLI hook report (mission W1, orca-adoption).
    //      Installed into a hosted agent CLI (Claude Code) via AgentHookInstaller;
    //      the hook invokes this so the GUI knows the agent's real state without
    //      scraping terminal output. Always fire-and-forget (no MMF round-trip).
    // =========================================================================
    private static int AgentHook(string[] args)
    {
        string evt = "", state = "", session = "", detail = "";
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--event"   when i + 1 < args.Length: evt = args[++i]; break;
                case "--state"   when i + 1 < args.Length: state = args[++i]; break;
                case "--session" when i + 1 < args.Length: session = args[++i]; break;
                case "--detail"  when i + 1 < args.Length: detail = args[++i]; break;
            }
        }

        if (string.IsNullOrWhiteSpace(evt))
        {
            Console.Error.WriteLine("Usage: agent-hook --event <name> [--state <phase>] [--session <id>] [--detail <text>]");
            return 1;
        }

        IntPtr agentWnd = FindAgentZero();
        if (agentWnd == IntPtr.Zero) return 1;

        var sb = new StringBuilder();
        sb.Append("{\"command\":\"agent-hook\"");
        sb.Append($",\"event\":\"{EscapeJson(evt)}\"");
        sb.Append($",\"state\":\"{EscapeJson(state)}\"");
        sb.Append($",\"session\":\"{EscapeJson(session)}\"");
        sb.Append($",\"detail\":\"{EscapeJson(detail)}\"");
        sb.Append('}');

        // Fire-and-forget by design — hooks fire on the agent's hot path, so we
        // never block them on an MMF response (mirrors --no-wait semantics).
        SendWpfCommand(agentWnd, sb.ToString());
        return 0;
    }

    // =========================================================================
    //  agent-hook-install / agent-hook-uninstall
    //      : install/remove AgentZero state-reporting hooks into every
    //      ~/.claude*/settings.json. Runs IN-PROCESS (no GUI/IPC needed) — this
    //      is the explicit, consented entry point for modifying Claude settings.
    // =========================================================================
    private static int AgentHookInstall()
    {
        var results = AgentZeroWpf.Services.AgentHookInstaller.InstallAll();
        if (results.Count == 0)
        {
            Console.WriteLine("No Claude Code profiles (~/.claude*) found.");
            return 0;
        }
        int failed = 0;
        foreach (var r in results)
        {
            if (r.Ok) Console.WriteLine($"  [{r.AccountKey}] {r.Action}" + (r.BackupPath is null ? "" : $"  (backup: {r.BackupPath})"));
            else { Console.Error.WriteLine($"  [{r.AccountKey}] FAILED: {r.Error}"); failed++; }
        }
        Console.WriteLine(failed == 0 ? "Agent hooks installed." : $"Completed with {failed} failure(s).");
        return failed == 0 ? 0 : 1;
    }

    private static int AgentHookUninstall()
    {
        var results = AgentZeroWpf.Services.AgentHookInstaller.UninstallAll();
        if (results.Count == 0)
        {
            Console.WriteLine("No Claude Code profiles (~/.claude*) found.");
            return 0;
        }
        int failed = 0;
        foreach (var r in results)
        {
            if (r.Ok) Console.WriteLine($"  [{r.AccountKey}] {r.Action}");
            else { Console.Error.WriteLine($"  [{r.AccountKey}] FAILED: {r.Error}"); failed++; }
        }
        Console.WriteLine(failed == 0 ? "Agent hooks removed." : $"Completed with {failed} failure(s).");
        return failed == 0 ? 0 : 1;
    }

    // =========================================================================
    //  trust-workspace [path]
    //      : pre-mark a folder as trusted in each agent CLI's trust store
    //      (Cursor / Copilot / Codex) so their "trust this folder?" prompt
    //      won't intercept injected keystrokes (mission W2, orca-adoption).
    //      Runs IN-PROCESS (no GUI). Explicit, consented — modifies ~/.cursor,
    //      ~/.copilot, ~/.codex. Defaults to the current directory.
    // =========================================================================
    private static int TrustWorkspace(string[] args)
    {
        var path = args.Length > 0 && !args[0].StartsWith("--")
            ? args[0]
            : Directory.GetCurrentDirectory();

        if (!Directory.Exists(path))
        {
            Console.Error.WriteLine($"Error: folder not found: {path}");
            return 1;
        }

        var results = Agent.Common.Agents.TrustPresetWriter.MarkAllTrusted(path);
        Console.WriteLine($"Trusting workspace: {System.IO.Path.GetFullPath(path)}");
        int failed = 0;
        foreach (var r in results)
        {
            if (r.Ok) Console.WriteLine($"  [{r.Agent}] {r.Detail}");
            else { Console.Error.WriteLine($"  [{r.Agent}] FAILED: {r.Detail}"); failed++; }
        }
        return failed == 0 ? 0 : 1;
    }

    // =========================================================================
    //  cost : estimated USD cost from recorded token usage (mission W9).
    //      Reads the local telemetry DB in-process (no GUI) and prints a
    //      per-model breakdown via TokenCostCalculator.
    // =========================================================================
    // The in-process DB CLI commands may run before the GUI ever launched with
    // this build, so the SQLite file might be missing the latest migrations.
    // Ensure it's created + migrated (idempotent; no-op if the GUI already did).
    private static void EnsureDbReady()
    {
        try { Agent.Common.Data.AppDbContext.InitializeDatabase(); }
        catch (Exception ex) { AppLogger.Log($"[CLI] EnsureDbReady failed: {ex.Message}"); }
    }

    private static int ShowCost()
    {
        try
        {
            EnsureDbReady();
            using var db = new Agent.Common.Data.AppDbContext();
            var records = db.TokenUsageRecords.ToList();
            if (records.Count == 0)
            {
                Console.WriteLine("No token usage recorded yet.");
                return 0;
            }
            var total = Agent.Common.Telemetry.TokenCostCalculator.TotalUsd(records);
            Console.WriteLine($"Estimated cost (all recorded turns): ${total:F2}  ({records.Count} turns)");
            Console.WriteLine("By model:");
            foreach (var (model, usd, count) in Agent.Common.Telemetry.TokenCostCalculator.ByModel(records))
                Console.WriteLine($"  {model,-32} ${usd,10:F2}   ({count} turns)");
            Console.WriteLine("(estimate — prices are editable defaults, not a live feed)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    // =========================================================================
    //  bot-signal <kind> [--from name] [--to name] [--message text...]
    //      : send structured peer signal to AgentBot broker
    // =========================================================================

    /// <summary>
    /// One-line build identity, useful for a quick "is this the build I think
    /// it is?" sanity check from any shell. Doesn't require the GUI to be up
    /// (no IPC) — pure local introspection of the running CLI binary.
    /// </summary>
    private static int PrintVersion()
    {
        Console.WriteLine($"AgentZero Lite CLI {AppVersionProvider.GetDisplayVersion()}");
        Console.WriteLine($"  exe : {GetSelfExePath()}");
        Console.WriteLine($"  base: {AppContext.BaseDirectory}");
        return 0;
    }

    private static string GetSelfExePath()
        => Process.GetCurrentProcess().MainModule?.FileName
           ?? Path.Combine(AppContext.BaseDirectory, "AgentZeroLite.exe");

    // help [topic] — with a known topic, serve the full agent skill guide
    // (mission W4/W5 anti-drift: the guide is served by the live binary, never
    // cached into the agent's skills folder). No topic → general usage.
    private static int Help(string[] args)
    {
        if (args.Length > 0 && !args[0].StartsWith("--"))
        {
            var topic = args[0];
            var guide = Agent.Common.Agents.AgentSkillGuides.Get(topic);
            if (guide is not null) { Console.WriteLine(guide); return 0; }
            Console.Error.WriteLine($"Unknown help topic: {topic}");
            Console.WriteLine("Topics: " + string.Join(", ", Agent.Common.Agents.AgentSkillGuides.Topics));
            return 1;
        }
        return PrintHelp();
    }

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
        // Header advertises which build of the CLI is responding so a stale
        // PATH entry vs the live one is obvious from the very first line of
        // any --help / unknown-command / no-args invocation. Same identity
        // is queryable directly via `version`.
        Console.WriteLine($"AgentZero Lite CLI {AppVersionProvider.GetDisplayVersion()}");
        Console.WriteLine($"  build: {GetSelfExePath()}");
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
        Console.WriteLine("  terminal-wait <grp> <tab> [--idle-ms N] Block until a terminal goes idle");
        Console.WriteLine("  worktree <list|add|remove> ...          Manage git worktrees (current repo)");
        Console.WriteLine("  orchestrate <list|create|status> ...    Supervised multi-agent runs (durable)");
        Console.WriteLine("  automation <create|list|remove|due>     Scheduled agent runs (every/hourly/daily)");
        Console.WriteLine("  skill-stub-install                      Inject anti-drift AgentZero skill stub");
        Console.WriteLine("  bot-chat <message> [--from name]        Display external chat in AgentBot");
        Console.WriteLine("  agent-hook --event <name> [--state p]   Report hosted-agent state (fire-and-forget)");
        Console.WriteLine("  agent-hook-install                      Install state hooks into ~/.claude*/settings.json");
        Console.WriteLine("  agent-hook-uninstall                    Remove AgentZero hooks from ~/.claude*/settings.json");
        Console.WriteLine("  trust-workspace [path]                  Pre-trust a folder for Cursor/Copilot/Codex CLIs");
        Console.WriteLine("  cost                                    Estimated USD cost from recorded token usage");
        Console.WriteLine("  os <verb> [args]                        OS-control: window/screenshot/input (see 'os help')");
        Console.WriteLine("  help                                    Show detailed help");
        Console.WriteLine("  version, --version, -v                  Print CLI build identity (no GUI needed)");
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
