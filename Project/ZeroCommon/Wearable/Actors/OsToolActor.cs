using System.Text.Json;
using System.Text.RegularExpressions;
using Akka.Actor;
using Akka.Event;
using Agent.Common.Llm.Tools;
using Agent.Common.Os;

namespace Agent.Common.Wearable.Actors;

/// <summary>
/// The desktop side of the watch's toolbelt: windows, screenshots, the mouse, the keyboard
/// and starting programs, through <see cref="IOsControl"/> — so this actor holds the rules
/// and never names a platform. The rules:
/// <list type="bullet">
///   <item>The whole surface is off unless <see cref="WearableSettings.OsControlEnabled"/>.</item>
///   <item>Screenshots are saved under <c>home/screenshots</c> and come back as an alias path —
///     the image never enters the model's context, the person can open it.</item>
///   <item><c>os_launch</c> takes a program NAME only (no path, no arguments, no shell or script
///     host), and the one file it may pass along must resolve inside an allowed folder. That is
///     the line between "open my note in Notepad" and "run this command line".</item>
///   <item>Every action is written to <see cref="OsAuditLog"/>.</item>
/// </list>
/// </summary>
public sealed partial class OsToolActor : ReceiveActor
{
    public sealed record ListWindows(string? TitleFilter);
    public sealed record Screenshot(long Hwnd, bool Grayscale);
    public sealed record Activate(long Hwnd);
    public sealed record Click(int X, int Y, bool Right, bool Double);
    public sealed record Key(string Spec);
    public sealed record TypeText(string Text);
    public sealed record Launch(string Program, string? File);

    public const string ScreenshotsFolder = "screenshots";

    /// <summary>Programs that would turn os_launch into "run anything": shells and script hosts.</summary>
    internal static readonly HashSet<string> RefusedPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "regedit",
        "bash", "sh", "zsh", "wsl", "python", "python3", "py", "node", "java", "javaw", "msiexec",
        "certutil", "bitsadmin", "schtasks", "sc", "reg", "wmic", "msbuild", "installutil", "dotnet",
    };

    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly IOsControl _os;
    private readonly AllowedRootResolver _roots;
    private readonly bool _enabled;
    private readonly OsAuditLog _audit;

    public OsToolActor(IOsControl os, AllowedRootResolver roots, bool enabled, OsAuditLog? audit = null)
    {
        _os = os;
        _roots = roots;
        _enabled = enabled;
        _audit = audit ?? new OsAuditLog("wearable");

        Receive<ListWindows>(m => Run("os_list_windows", m, () => ListWindowsJson(m.TitleFilter)));
        Receive<Screenshot>(m => Run("os_screenshot", m, () => ScreenshotJson(m.Hwnd, m.Grayscale)));
        Receive<Activate>(m => Run("os_activate", m, () => _os.Activate(m.Hwnd)
            ? Ok(new { ok = true, hwnd = m.Hwnd })
            : ToolJson.Fail("no such window (list it again with os_list_windows)")));
        Receive<Click>(m => Run("os_mouse_click", m, () =>
        {
            _os.Click(m.X, m.Y, m.Right, m.Double);
            return Ok(new { ok = true, x = m.X, y = m.Y, right = m.Right, @double = m.Double });
        }));
        Receive<Key>(m => Run("os_key_press", m, () => _os.KeyPress(m.Spec)
            ? Ok(new { ok = true, key = m.Spec })
            : ToolJson.Fail($"key spec not understood: '{m.Spec}' (modifiers ctrl/alt/shift/win + one key: a-z, 0-9, enter, tab, esc, space, backspace, del, home, end, pgup, pgdn, arrows, f1-f12)")));
        Receive<TypeText>(m => Run("os_type_text", new { chars = m.Text.Length }, () =>
        {
            _os.TypeText(m.Text);
            return Ok(new { ok = true, typed = m.Text.Length });
        }));
        Receive<Launch>(m => Run("os_launch", m, () => LaunchJson(m.Program, m.File)));
    }

    private void Run(string verb, object args, Func<string> body)
    {
        string result;
        if (!_enabled)
            result = ToolJson.Fail("OS control is turned off for the watch (Wearable page → OS control)");
        else if (!_os.Supported)
            result = ToolJson.Fail($"OS control is not available on this OS yet ({_os.Platform})");
        else
        {
            try { result = body(); }
            catch (Exception ex)
            {
                _log.Warning("{0} threw: {1}", verb, ex.Message);
                result = ToolJson.Fail(ex.Message);
            }
        }
        var ok = result.StartsWith("{\"ok\":true", StringComparison.Ordinal);
        _audit.Record(verb, args, ok, ok ? null : Head(result, 200));
        _log.Info("{0} -> {1}", verb, ok ? "ok" : Head(result, 120));
        Sender.Tell(result, Self);
    }

    private string ListWindowsJson(string? filter)
    {
        var windows = _os.ListWindows(string.IsNullOrWhiteSpace(filter) ? null : filter);
        return Ok(new
        {
            ok = true,
            count = windows.Count,
            windows = windows.Take(40).Select(w => new
            {
                hwnd = w.Hwnd, title = w.Title, process = w.Process,
                x = w.X, y = w.Y, w = w.Width, h = w.Height, minimized = w.Minimized,
            }),
        });
    }

    private string ScreenshotJson(long hwnd, bool grayscale)
    {
        var home = _roots.Find(WearableSettings.HomeAlias);
        if (home is null) return ToolJson.Fail("no home folder to save the screenshot in");
        var capture = _os.Capture(hwnd, grayscale);
        if (capture is null) return ToolJson.Fail("that window cannot be captured (minimized, gone or zero-sized)");
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}{(hwnd == 0 ? "-desktop" : $"-{hwnd}")}.png";
        var dir = Path.Combine(home.Path, ScreenshotsFolder);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), capture.Png);
        return Ok(new
        {
            ok = true,
            path = AllowedRootResolver.Prefix(home.Alias, $"{ScreenshotsFolder}/{name}"),
            width = capture.Width,
            height = capture.Height,
            screen_width = capture.SourceWidth,
            screen_height = capture.SourceHeight,
            note = "saved on the PC; open_file shows it there. Window coordinates from os_list_windows are what os_mouse_click takes.",
        });
    }

    private string LaunchJson(string program, string? file)
    {
        var name = (program ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (!ProgramName().IsMatch(name))
            return ToolJson.Fail("give the program by name only, e.g. \"notepad\" — no path, no arguments");
        if (RefusedPrograms.Contains(name))
            return ToolJson.Fail($"'{name}' is a shell or script host; os_launch does not start those");

        string? full = null;
        string? aliasPath = null;
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!_roots.TryResolve(file, out var root, out var rel, out var error)) return ToolJson.Fail(error);
            if (!FileToolCore.TryResolveInsideRoot(root.Path, rel, out full, out error)) return ToolJson.Fail(error);
            if (!File.Exists(full) && !root.Writable)
                return ToolJson.Fail("that file does not exist, and its folder is read-only");
            aliasPath = AllowedRootResolver.Prefix(root.Alias, rel);
        }

        var pid = _os.Launch(name, full);
        return Ok(new { ok = true, started = name, pid, file = aliasPath });
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex ProgramName();

    private static string Ok(object value) => JsonSerializer.Serialize(value, ToolJson.Options);

    private static string Head(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
