using System.Text.Json;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Agent.Common.Llm.Tools;
using Agent.Common.Os;
using Agent.Common.Wearable;
using Agent.Common.Wearable.Actors;
using Xunit;

namespace ZeroCommon.Tests.Wearable;

/// <summary>
/// The watch's work folder (<c>home</c>), notes and delete, and the rules the desktop tools
/// apply on top of <see cref="IOsControl"/>. The platform is faked: nothing here moves the
/// real mouse, presses a key or starts a program.
/// </summary>
public sealed class HomeAndOsToolsTests : TestKit, IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "azl-home-" + Guid.NewGuid().ToString("N"));
    private readonly string _music = Path.Combine(Path.GetTempPath(), "azl-music-" + Guid.NewGuid().ToString("N"));

    public HomeAndOsToolsTests()
    {
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_music);
        File.WriteAllText(Path.Combine(_music, "song.txt"), "la");
    }

    void IDisposable.Dispose()
    {
        try { Directory.Delete(_home, true); } catch { }
        try { Directory.Delete(_music, true); } catch { }
    }

    private AllowedRootResolver Roots() => new([
        new AllowedRoot { Alias = WearableSettings.HomeAlias, Path = _home, Writable = true },
        new AllowedRoot { Alias = "music", Path = _music, Writable = false },
    ]);

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    // ── home ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Home_comes_first_and_a_chosen_folder_cannot_take_its_alias()
    {
        var s = new WearableSettings { AllowedRoots = [new AllowedRoot { Alias = "home", Path = @"C:\x" }] }.Normalize();
        Assert.Equal("home-2", s.AllowedRoots[0].Alias);
        var roots = s.RootsWithHome().ToList();
        Assert.Equal(WearableSettings.HomeAlias, roots[0].Alias);
        Assert.True(roots[0].Writable);
        Assert.Equal(WearableSettings.HomeDirectory, roots[0].Path);
    }

    [Fact]
    public void A_note_is_saved_under_home_notes_appended_listed_and_read_back()
    {
        var roots = Roots();
        var saved = Json(AllowedRootFileTools.NoteSave(roots, "장보기 목록", "우유", append: false));
        Assert.True(saved.GetProperty("ok").GetBoolean());
        Assert.Equal("home/notes/장보기 목록.txt", saved.GetProperty("path").GetString());
        AllowedRootFileTools.NoteSave(roots, "장보기 목록", "계란", append: true);

        Assert.Equal("우유" + Environment.NewLine + "계란", File.ReadAllText(Path.Combine(_home, "notes", "장보기 목록.txt")));
        var list = Json(AllowedRootFileTools.NoteRead(roots, null, 4096));
        Assert.Equal(1, list.GetProperty("count").GetInt32());
        var read = AllowedRootFileTools.NoteRead(roots, "장보기 목록", 4096);
        Assert.Contains("계란", read);
    }

    [Fact]
    public void A_note_title_cannot_climb_out_of_the_notes_folder()
    {
        Assert.Equal("secret.txt", AllowedRootFileTools.NoteFileName("../../secret"));   // separators dropped, leading dots trimmed
        Assert.DoesNotContain('/', AllowedRootFileTools.NoteFileName("a/b\\c:d"));
        Assert.StartsWith("note-", AllowedRootFileTools.NoteFileName("   "));
    }

    [Fact]
    public void Delete_needs_a_writable_folder_and_removes_files_only()
    {
        var roots = Roots();
        Directory.CreateDirectory(Path.Combine(_home, "sub"));
        File.WriteAllText(Path.Combine(_home, "old.txt"), "x");

        Assert.False(Json(AllowedRootFileTools.DeleteFile(roots, "music/song.txt")).GetProperty("ok").GetBoolean());
        Assert.True(File.Exists(Path.Combine(_music, "song.txt")));
        Assert.Contains("folder", AllowedRootFileTools.DeleteFile(roots, "home/sub"));
        Assert.True(Json(AllowedRootFileTools.DeleteFile(roots, "home/old.txt")).GetProperty("ok").GetBoolean());
        Assert.False(File.Exists(Path.Combine(_home, "old.txt")));
    }

    // ── desktop rules ─────────────────────────────────────────────────────────

    private sealed class FakeOs : IOsControl
    {
        public List<string> Calls { get; } = [];
        public bool Supported => true;
        public string Platform => "fake";
        public IReadOnlyList<OsWindow> ListWindows(string? titleFilter = null)
            => [new OsWindow(42, "제목 없음 - 메모장", "Notepad", 7, "notepad", 10, 20, 800, 600, false)];
        public bool Activate(long hwnd) { Calls.Add($"activate {hwnd}"); return hwnd == 42; }
        public OsWindow? Foreground() => new(7, "Discord", "Chrome", 1, "discord", 0, 0, 10, 10, false);
        public bool Close(long hwnd, TimeSpan wait) { Calls.Add($"close {hwnd}"); return hwnd == 42; }
        public OsCapture? Capture(long hwnd, bool grayscale, int maxWidth = 1920, int maxHeight = 1080)
            => new(PngEncoder.Encode(new byte[4 * 2], 4, 2, gray: true), 4, 2, 4, 2);
        public void Click(int x, int y, bool right = false, bool doubleClick = false) => Calls.Add($"click {x},{y}");
        public bool KeyPress(string spec) { Calls.Add($"key {spec}"); return KeySpec.TryParse(spec, out _, out _); }
        public void TypeText(string text) => Calls.Add($"type {text}");
        public int Launch(string program, string? filePath = null) { Calls.Add($"launch {program} {filePath}"); return 1234; }
    }

    private (IActorRef Actor, FakeOs Os, string AuditDir) OsActor(bool enabled = true)
    {
        var os = new FakeOs();
        var audit = Path.Combine(_home, "audit");
        var roots = Roots();
        var actor = Sys.ActorOf(Props.Create(() => new OsToolActor(os, roots, enabled, new OsAuditLog("test", audit))));
        return (actor, os, audit);
    }

    private JsonElement AskOs(IActorRef actor, object message)
        => Json(actor.Ask<string>(message, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());

    [Fact]
    public void Turned_off_means_nothing_is_touched()
    {
        var (actor, os, _) = OsActor(enabled: false);
        var r = AskOs(actor, new OsToolActor.Click(1, 2, false, false));
        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Contains("turned off", r.GetProperty("error").GetString());
        Assert.Empty(os.Calls);
    }

    [Fact]
    public void Launch_takes_a_program_name_only_and_never_a_shell()
    {
        var (actor, os, _) = OsActor();
        Assert.False(AskOs(actor, new OsToolActor.Launch(@"C:\Windows\System32\notepad.exe", null)).GetProperty("ok").GetBoolean());
        Assert.False(AskOs(actor, new OsToolActor.Launch("notepad evil.txt", null)).GetProperty("ok").GetBoolean());
        Assert.False(AskOs(actor, new OsToolActor.Launch("powershell", null)).GetProperty("ok").GetBoolean());
        Assert.False(AskOs(actor, new OsToolActor.Launch("cmd.exe", null)).GetProperty("ok").GetBoolean());
        Assert.Empty(os.Calls);

        var ok = AskOs(actor, new OsToolActor.Launch("notepad.exe", null));
        Assert.True(ok.GetProperty("ok").GetBoolean());
        Assert.Equal("launch notepad ", os.Calls.Single());
    }

    [Fact]
    public void Launch_passes_only_a_file_inside_the_allowed_folders()
    {
        var (actor, os, _) = OsActor();
        AllowedRootFileTools.NoteSave(Roots(), "todo", "a", false);

        Assert.False(AskOs(actor, new OsToolActor.Launch("notepad", @"C:\Windows\win.ini")).GetProperty("ok").GetBoolean());
        Assert.False(AskOs(actor, new OsToolActor.Launch("notepad", "home/../../x.txt")).GetProperty("ok").GetBoolean());
        Assert.False(AskOs(actor, new OsToolActor.Launch("notepad", "music/new.txt")).GetProperty("ok").GetBoolean());   // read-only, absent

        var r = AskOs(actor, new OsToolActor.Launch("notepad", "home/notes/todo.txt"));
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal("home/notes/todo.txt", r.GetProperty("file").GetString());
        Assert.Equal($"launch notepad {Path.Combine(_home, "notes", "todo.txt")}", os.Calls.Single());
    }

    [Fact]
    public void A_screenshot_lands_in_home_screenshots_and_every_action_is_audited()
    {
        var (actor, _, audit) = OsActor();
        var shot = AskOs(actor, new OsToolActor.Screenshot(0, true));
        Assert.True(shot.GetProperty("ok").GetBoolean());
        var path = shot.GetProperty("path").GetString()!;
        Assert.StartsWith("home/screenshots/", path);
        var file = Path.Combine(_home, path["home/".Length..].Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], File.ReadAllBytes(file)[..4]);

        AskOs(actor, new OsToolActor.Key("ctrl+nope"));
        var lines = File.ReadAllLines(Directory.GetFiles(audit).Single());
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"verb\":\"os_key_press\"", lines[1]);
        Assert.Contains("\"ok\":false", lines[1]);
    }

    [Fact]
    public void An_activate_that_did_not_bring_the_window_forward_is_a_failure_that_names_the_focus()
    {
        var (actor, _, _) = OsActor();
        Assert.True(AskOs(actor, new OsToolActor.Activate(42)).GetProperty("ok").GetBoolean());
        var refused = AskOs(actor, new OsToolActor.Activate(99));
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Contains("Discord", refused.GetProperty("error").GetString());
    }

    [Fact]
    public void A_key_press_says_which_window_it_landed_in()
    {
        var (actor, _, _) = OsActor();
        var r = AskOs(actor, new OsToolActor.Key("ctrl+s"));
        Assert.Equal("Discord", r.GetProperty("sent_to").GetString());
    }

    [Fact]
    public void Close_reports_a_window_that_stayed_open_as_not_closed()
    {
        var (actor, os, _) = OsActor();
        Assert.True(AskOs(actor, new OsToolActor.CloseWindow(42)).GetProperty("closed").GetBoolean());
        var stuck = AskOs(actor, new OsToolActor.CloseWindow(43));
        Assert.False(stuck.GetProperty("ok").GetBoolean());
        Assert.False(stuck.GetProperty("closed").GetBoolean());
        Assert.Equal(["close 42", "close 43"], os.Calls);
    }

    [Fact]
    public void Key_specs_parse_like_the_wpf_host()
    {
        Assert.True(KeySpec.TryParse("ctrl+shift+t", out var mods, out var key));
        Assert.Equal([0x11, 0x10], mods);
        Assert.Equal((byte)'T', key);
        Assert.True(KeySpec.TryParse("F12", out _, out var f12));
        Assert.Equal(0x7B, f12);
        Assert.False(KeySpec.TryParse("ctrl+a+b", out _, out _));
        Assert.False(KeySpec.TryParse("ctrl", out _, out _));
    }
}
