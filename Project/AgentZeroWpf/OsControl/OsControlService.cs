using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentZeroWpf.Module;

namespace AgentZeroWpf.OsControl;

/// <summary>
/// Single facade behind which all OS-control verbs live. Both CLI dispatch
/// and the LLM toolbelt funnel through here so behaviour is identical no
/// matter who's calling. Output is JSON-shaped throughout (every method
/// returns a JSON string) — CLI pretty-prints, LLM consumes raw.
/// </summary>
internal static class OsControlService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    // -------------------------------------------------------------- READ-ONLY

    public static string ListWindows(string? titleFilter = null, bool includeHidden = false, OsAuditLog.Caller caller = OsAuditLog.Caller.Cli)
    {
        try
        {
            var list = WindowEnumerator.EnumerateTopLevel(titleFilter, includeHidden);
            OsAuditLog.Record(caller, "list_windows", new { titleFilter, includeHidden, count = list.Count });
            return JsonSerializer.Serialize(new { ok = true, count = list.Count, windows = list }, JsonOpts);
        }
        catch (Exception ex)
        {
            OsAuditLog.Record(caller, "list_windows", null, ok: false, error: ex.Message);
            return Err(ex);
        }
    }

    public static string GetWindowInfo(long hwnd, OsAuditLog.Caller caller = OsAuditLog.Caller.Cli)
    {
        try
        {
            var match = WindowEnumerator.EnumerateTopLevel(includeHidden: true)
                .FirstOrDefault(w => w.Handle == hwnd);
            if (match is null)
            {
                OsAuditLog.Record(caller, "get_window_info", new { hwnd }, ok: false, error: "window not found");
                return JsonSerializer.Serialize(new { ok = false, error = "window not found" }, JsonOpts);
            }
            OsAuditLog.Record(caller, "get_window_info", new { hwnd });
            return JsonSerializer.Serialize(new { ok = true, window = match }, JsonOpts);
        }
        catch (Exception ex)
        {
            OsAuditLog.Record(caller, "get_window_info", new { hwnd }, ok: false, error: ex.Message);
            return Err(ex);
        }
    }

    public static string Dpi(OsAuditLog.Caller caller = OsAuditLog.Caller.Cli)
    {
        try
        {
            var info = DpiHelper.Query();
            OsAuditLog.Record(caller, "dpi");
            return JsonSerializer.Serialize(new { ok = true, dpi = info }, JsonOpts);
        }
        catch (Exception ex)
        {
            OsAuditLog.Record(caller, "dpi", null, ok: false, error: ex.Message);
            return Err(ex);
        }
    }

    public static string Screenshot(long hwnd, bool grayscale, bool fullDesktop, OsAuditLog.Caller caller = OsAuditLog.Caller.Cli)
    {
        try
        {
            ScreenshotService.CaptureResult? r;
            string label;
            if (fullDesktop || hwnd == 0)
            {
                label = "desktop";
                r = ScreenshotService.CaptureDesktop(label, grayscale);
            }
            else
            {
                label = $"hwnd-{hwnd}";
                r = ScreenshotService.CaptureWindow((IntPtr)hwnd, label, grayscale);
                if (r is null)
                {
                    OsAuditLog.Record(caller, "screenshot", new { hwnd, grayscale, fullDesktop }, ok: false, error: "window not capturable");
                    return JsonSerializer.Serialize(new { ok = false, error = "window not capturable (minimized or zero-size)" }, JsonOpts);
                }
            }

            OsAuditLog.Record(caller, "screenshot", new { hwnd, grayscale, fullDesktop, path = r.Path });
            return JsonSerializer.Serialize(new { ok = true, path = r.Path, width = r.Width, height = r.Height, grayscale = r.Grayscale }, JsonOpts);
        }
        catch (Exception ex)
        {
            OsAuditLog.Record(caller, "screenshot", new { hwnd, grayscale, fullDesktop }, ok: false, error: ex.Message);
            return Err(ex);
        }
    }

    public static async Task<string> ElementTreeAsync(long hwnd, int maxDepth, string? search, OsAuditLog.Caller caller = OsAuditLog.Caller.Cli)
    {
        try
        {
            // ElementTreeScanner uses System.Windows.Automation which requires
            // an STA thread. Marshall onto a fresh STA thread to keep callers
            // (Akka / async LLM loop) happy.
            var result = await Task.Run(() =>
            {
                ElementTreeScanResult? local = null;
                var t = new System.Threading.Thread(() =>
                {
                    local = ElementTreeScanner.Scan((IntPtr)hwnd, maxDepth);
                });
                t.SetApartmentState(System.Threading.ApartmentState.STA);
                t.Start();
                t.Join();
                return local;
            });

            if (result is null)
            {
                OsAuditLog.Record(caller, "element_tree", new { hwnd, maxDepth, search }, ok: false, error: "scan failed");
                return JsonSerializer.Serialize(new { ok = false, error = "element tree scan failed (window invalid or UIA unavailable)" }, JsonOpts);
            }

            string treeText = result.TreeText;
            if (!string.IsNullOrEmpty(search))
            {
                treeText = string.Join('\n',
                    treeText.Split('\n').Where(l => l.Contains(search, StringComparison.OrdinalIgnoreCase)));
            }

            OsAuditLog.Record(caller, "element_tree", new { hwnd, maxDepth, search, nodeCount = result.NodeCount });
            return JsonSerializer.Serialize(new
            {
                ok = true,
                hwnd,
                nodeCount = result.NodeCount,
                tree = treeText,
            }, JsonOpts);
        }
        catch (Exception ex)
        {
            OsAuditLog.Record(caller, "element_tree", new { hwnd, maxDepth, search }, ok: false, error: ex.Message);
            return Err(ex);
        }
    }

    public static string TextCapture(long hwnd, OsAuditLog.Caller caller = OsAuditLog.Caller.Cli)
    {
        // Phase A note: the AgentWin TextCaptureService used a richer STA-
        // dispatched path with scroll attempts. For Lite's first cut we
        // reuse ElementTreeScanner — it walks the same UIAutomation tree and
        // gathers .Name properties which is what TextCapture mostly returns.
        // A scroll-aware variant lives in scope of a follow-up mission.
        try
        {
            var result = ElementTreeScanner.Scan((IntPtr)hwnd, maxDepth: 30, maxLogLines: 0);
            if (result is null)
            {
                OsAuditLog.Record(caller, "text_capture", new { hwnd }, ok: false, error: "scan failed");
                return JsonSerializer.Serialize(new { ok = false, error = "text capture failed" }, JsonOpts);
            }
            OsAuditLog.Record(caller, "text_capture", new { hwnd, nodeCount = result.NodeCount });
            return JsonSerializer.Serialize(new { ok = true, hwnd, text = result.TreeText, nodeCount = result.NodeCount }, JsonOpts);
        }
        catch (Exception ex)
        {
            OsAuditLog.Record(caller, "text_capture", new { hwnd }, ok: false, error: ex.Message);
            return Err(ex);
        }
    }

    // -------------------------------------------------------------- WRITE (gated)

    public static string Activate(long hwnd, OsAuditLog.Caller caller = OsAuditLog.Caller.Cli)
    {
        try
        {
            bool ok = WindowEnumerator.Activate((IntPtr)hwnd);
            OsAuditLog.Record(caller, "activate", new { hwnd }, ok: ok);
            return JsonSerializer.Serialize(new { ok, hwnd }, JsonOpts);
        }
        catch (Exception ex)
        {
            OsAuditLog.Record(caller, "activate", new { hwnd }, ok: false, error: ex.Message);
            return Err(ex);
        }
    }

    public static string MouseClick(int x, int y, bool right, bool dbl, bool inputAllowed, OsAuditLog.Caller caller)
    {
        if (!inputAllowed)
        {
            OsAuditLog.Record(caller, "mouse_click", new { x, y, right, dbl }, ok: false, error: "input gate denied");
            return Denied("mouse_click");
        }
        try
        {
            InputSimulator.MouseClick(x, y, right, dbl);
            OsAuditLog.Record(caller, "mouse_click", new { x, y, right, dbl });
            return JsonSerializer.Serialize(new { ok = true, x, y, right, dbl }, JsonOpts);
        }
        catch (Exception ex)
        {
            OsAuditLog.Record(caller, "mouse_click", new { x, y, right, dbl }, ok: false, error: ex.Message);
            return Err(ex);
        }
    }

    public static string MouseMove(int x, int y, bool inputAllowed, OsAuditLog.Caller caller)
    {
        if (!inputAllowed)
        {
            OsAuditLog.Record(caller, "mouse_move", new { x, y }, ok: false, error: "input gate denied");
            return Denied("mouse_move");
        }
        try
        {
            InputSimulator.MouseMove(x, y);
            OsAuditLog.Record(caller, "mouse_move", new { x, y });
            return JsonSerializer.Serialize(new { ok = true, x, y }, JsonOpts);
        }
        catch (Exception ex)
        {
            OsAuditLog.Record(caller, "mouse_move", new { x, y }, ok: false, error: ex.Message);
            return Err(ex);
        }
    }

    public static string MouseWheel(int x, int y, int delta, bool inputAllowed, OsAuditLog.Caller caller)
    {
        if (!inputAllowed)
        {
            OsAuditLog.Record(caller, "mouse_wheel", new { x, y, delta }, ok: false, error: "input gate denied");
            return Denied("mouse_wheel");
        }
        try
        {
            InputSimulator.MouseWheel(x, y, delta);
            OsAuditLog.Record(caller, "mouse_wheel", new { x, y, delta });
            return JsonSerializer.Serialize(new { ok = true, x, y, delta }, JsonOpts);
        }
        catch (Exception ex)
        {
            OsAuditLog.Record(caller, "mouse_wheel", new { x, y, delta }, ok: false, error: ex.Message);
            return Err(ex);
        }
    }

    public static string KeyPress(string keySpec, bool inputAllowed, OsAuditLog.Caller caller)
    {
        if (!inputAllowed)
        {
            OsAuditLog.Record(caller, "key_press", new { keySpec }, ok: false, error: "input gate denied");
            return Denied("key_press");
        }
        try
        {
            bool ok = InputSimulator.KeyPress(keySpec);
            OsAuditLog.Record(caller, "key_press", new { keySpec }, ok: ok, error: ok ? null : "unparseable key spec");
            return JsonSerializer.Serialize(new { ok, key = keySpec }, JsonOpts);
        }
        catch (Exception ex)
        {
            OsAuditLog.Record(caller, "key_press", new { keySpec }, ok: false, error: ex.Message);
            return Err(ex);
        }
    }

    private static string Denied(string verb)
        => JsonSerializer.Serialize(new { ok = false, error = OsApprovalGate.DenialMessage, verb }, JsonOpts);

    private static string Err(Exception ex)
        => JsonSerializer.Serialize(new { ok = false, error = ex.Message, type = ex.GetType().Name }, JsonOpts);
}
