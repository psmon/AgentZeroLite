using System;
using System.Globalization;
using System.Text.Json;

namespace AgentZeroWpf.OsControl;

/// <summary>
/// CLI dispatcher for the <c>os</c> command group. Mission M0014 brings the
/// AgentWin (origin) OS-automation surface into Lite's CLI; this is its
/// home. Verbs are lowercase kebab-case (matches the rest of CliHandler).
///
/// Critical: these run **in-process** in the CLI invocation. Unlike
/// <c>terminal-list</c> which round-trips to the live GUI via WM_COPYDATA,
/// OS-control needs no GUI state — running them locally is faster and
/// works even when the GUI isn't up. Audit log lands under
/// <c>tmp/os-cli/audit/</c> regardless.
/// </summary>
internal static class OsCliCommands
{
    public static int Dispatch(string[] args)
    {
        if (args.Length == 0)
        {
            PrintOsUsage();
            return 1;
        }

        var verb = args[0].ToLowerInvariant();
        var rest = args.AsSpan(1).ToArray();

        return verb switch
        {
            "help" or "--help" or "-h"  => PrintOsUsageOk(),
            "list-windows"               => ListWindows(rest),
            "get-window-info"            => GetWindowInfo(rest),
            "screenshot"                 => Screenshot(rest),
            "element-tree"               => ElementTree(rest),
            "text-capture"               => TextCapture(rest),
            "dpi"                        => Dpi(rest),
            "activate"                   => Activate(rest),
            "mouse-click"                => MouseClick(rest),
            "mouse-move"                 => MouseMove(rest),
            "mouse-wheel"                => MouseWheel(rest),
            "keypress"                   => KeyPress(rest),
            "audit"                      => ShowAudit(rest),
            _ => UnknownVerb(verb),
        };
    }

    // -------------------------------------------------------- read-only verbs

    private static int ListWindows(string[] args)
    {
        string? filter = null;
        bool includeHidden = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--filter" when i + 1 < args.Length:
                    filter = args[++i]; break;
                case "--include-hidden":
                    includeHidden = true; break;
            }
        }
        var json = OsControlService.ListWindows(filter, includeHidden, OsAuditLog.Caller.Cli);
        return PrintJson(json);
    }

    private static int GetWindowInfo(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: os get-window-info <hwnd>");
            return 1;
        }
        if (!TryParseHwnd(args[0], out long hwnd))
        {
            Console.Error.WriteLine($"Error: invalid hwnd '{args[0]}' (decimal or 0x-prefixed hex)");
            return 1;
        }
        return PrintJson(OsControlService.GetWindowInfo(hwnd, OsAuditLog.Caller.Cli));
    }

    private static int Screenshot(string[] args)
    {
        long hwnd = 0;
        bool grayscale = true;
        bool fullDesktop = true;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--hwnd" when i + 1 < args.Length:
                    if (!TryParseHwnd(args[++i], out hwnd))
                    {
                        Console.Error.WriteLine($"Error: invalid hwnd");
                        return 1;
                    }
                    fullDesktop = false; break;
                case "--color":
                    grayscale = false; break;
                case "--gray":
                case "--grayscale":
                    grayscale = true; break;
                case "--full" or "--desktop":
                    fullDesktop = true; hwnd = 0; break;
            }
        }
        return PrintJson(OsControlService.Screenshot(hwnd, grayscale, fullDesktop, OsAuditLog.Caller.Cli));
    }

    private static int ElementTree(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: os element-tree <hwnd> [--depth N] [--search keyword]");
            return 1;
        }
        if (!TryParseHwnd(args[0], out long hwnd))
        {
            Console.Error.WriteLine($"Error: invalid hwnd '{args[0]}'");
            return 1;
        }
        int depth = 30;
        string? search = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--depth" when i + 1 < args.Length && int.TryParse(args[i + 1], out int d):
                    depth = Math.Clamp(d, 1, 100); i++; break;
                case "--search" when i + 1 < args.Length:
                    search = args[++i]; break;
            }
        }
        var json = OsControlService.ElementTreeAsync(hwnd, depth, search, OsAuditLog.Caller.Cli)
            .GetAwaiter().GetResult();
        return PrintJson(json);
    }

    private static int TextCapture(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: os text-capture <hwnd>");
            return 1;
        }
        if (!TryParseHwnd(args[0], out long hwnd)) return 1;
        return PrintJson(OsControlService.TextCapture(hwnd, OsAuditLog.Caller.Cli));
    }

    private static int Dpi(string[] _) => PrintJson(OsControlService.Dpi(OsAuditLog.Caller.Cli));

    private static int Activate(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: os activate <hwnd>");
            return 1;
        }
        if (!TryParseHwnd(args[0], out long hwnd)) return 1;
        return PrintJson(OsControlService.Activate(hwnd, OsAuditLog.Caller.Cli));
    }

    // -------------------------------------------------------- input simulation (gated)

    private static int MouseClick(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: os mouse-click <x> <y> [--right] [--double] [--allow-input]");
            return 1;
        }
        if (!int.TryParse(args[0], out int x) || !int.TryParse(args[1], out int y))
        {
            Console.Error.WriteLine("Error: x/y must be integers");
            return 1;
        }
        bool right = false, dbl = false, allow = false;
        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--right": right = true; break;
                case "--double": dbl = true; break;
                case "--allow-input": allow = true; break;
            }
        }
        bool gateOk = allow || OsApprovalGate.IsInputAllowedByEnv();
        return PrintJson(OsControlService.MouseClick(x, y, right, dbl, gateOk, OsAuditLog.Caller.Cli));
    }

    private static int MouseMove(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: os mouse-move <x> <y> [--allow-input]");
            return 1;
        }
        if (!int.TryParse(args[0], out int x) || !int.TryParse(args[1], out int y)) return 1;
        bool allow = args.Length > 2 && args[2].Equals("--allow-input", StringComparison.OrdinalIgnoreCase);
        bool gateOk = allow || OsApprovalGate.IsInputAllowedByEnv();
        return PrintJson(OsControlService.MouseMove(x, y, gateOk, OsAuditLog.Caller.Cli));
    }

    private static int MouseWheel(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: os mouse-wheel <x> <y> <delta> [--allow-input]");
            return 1;
        }
        if (!int.TryParse(args[0], out int x)
            || !int.TryParse(args[1], out int y)
            || !int.TryParse(args[2], out int delta)) return 1;
        bool allow = args.Length > 3 && args[3].Equals("--allow-input", StringComparison.OrdinalIgnoreCase);
        bool gateOk = allow || OsApprovalGate.IsInputAllowedByEnv();
        return PrintJson(OsControlService.MouseWheel(x, y, delta, gateOk, OsAuditLog.Caller.Cli));
    }

    private static int KeyPress(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: os keypress <keyspec> [--allow-input]");
            Console.Error.WriteLine("  Examples: 'a', 'ctrl+c', 'alt+f4', 'f5', 'escape', 'ctrl+shift+t'");
            return 1;
        }
        var keySpec = args[0];
        bool allow = args.Length > 1 && args[1].Equals("--allow-input", StringComparison.OrdinalIgnoreCase);
        bool gateOk = allow || OsApprovalGate.IsInputAllowedByEnv();
        return PrintJson(OsControlService.KeyPress(keySpec, gateOk, OsAuditLog.Caller.Cli));
    }

    // -------------------------------------------------------- audit

    private static int ShowAudit(string[] args)
    {
        var dir = OsControlPaths.AuditDir();
        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var file = System.IO.Path.Combine(dir, $"{today}.jsonl");

        int last = 20;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--last", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                && int.TryParse(args[++i], out int n))
                last = Math.Clamp(n, 1, 1000);
        }

        if (!System.IO.File.Exists(file))
        {
            Console.WriteLine($"No audit entries for {today} ({file})");
            return 0;
        }

        var lines = System.IO.File.ReadAllLines(file);
        Console.WriteLine($"=== OS audit (last {Math.Min(last, lines.Length)} of {lines.Length}, {today}) ===");
        Console.WriteLine();
        foreach (var line in lines.TakeLast(last))
            Console.WriteLine(line);
        return 0;
    }

    // -------------------------------------------------------- helpers

    private static bool TryParseHwnd(string s, out long hwnd)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hwnd);
        return long.TryParse(s, out hwnd);
    }

    private static int PrintJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var pretty = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(pretty);
            bool ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            return ok ? 0 : 1;
        }
        catch
        {
            Console.WriteLine(json);
            return 0;
        }
    }

    private static int UnknownVerb(string v)
    {
        Console.Error.WriteLine($"Unknown os verb: {v}");
        Console.WriteLine();
        PrintOsUsage();
        return 1;
    }

    private static int PrintOsUsageOk()
    {
        PrintOsUsage();
        return 0;
    }

    public static void PrintOsUsage()
    {
        Console.WriteLine("Usage: AgentZeroLite.exe -cli os <verb> [args]");
        Console.WriteLine();
        Console.WriteLine("Read-only verbs (always available):");
        Console.WriteLine("  list-windows [--filter S] [--include-hidden]    Enumerate visible top-level windows");
        Console.WriteLine("  get-window-info <hwnd>                          Detail one window");
        Console.WriteLine("  screenshot [--hwnd N] [--color] [--full]        Capture PNG to tmp/os-cli/screenshots/");
        Console.WriteLine("  element-tree <hwnd> [--depth N] [--search S]    UI Automation tree dump");
        Console.WriteLine("  text-capture <hwnd>                             Text from a window's UI tree");
        Console.WriteLine("  dpi                                             System + monitor DPI report");
        Console.WriteLine("  activate <hwnd>                                 Bring window to foreground");
        Console.WriteLine();
        Console.WriteLine("Input-simulation verbs (gated — require --allow-input or AGENTZERO_OS_INPUT_ALLOWED=1):");
        Console.WriteLine("  mouse-click <x> <y> [--right] [--double]");
        Console.WriteLine("  mouse-move <x> <y>");
        Console.WriteLine("  mouse-wheel <x> <y> <delta>");
        Console.WriteLine("  keypress <spec>                                 e.g. 'ctrl+c', 'alt+f4', 'f5'");
        Console.WriteLine();
        Console.WriteLine("Inspection:");
        Console.WriteLine("  audit [--last N]                                Show today's OS-control audit log");
        Console.WriteLine();
        Console.WriteLine("All verbs return JSON to stdout. Screenshots/audit land under tmp/os-cli/.");
    }
}
