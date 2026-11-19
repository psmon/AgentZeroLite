using System.IO;
using System.Text.Json;
using Agent.Common.Agents;
using Xunit;

namespace ZeroCommon.Tests;

/// <summary>
/// The garden harness (kakashi-harness) as the Agent Stage sees it: finding it above a Claude
/// session's folder, telling an expert sub-agent from a survey, and the experts the main
/// conversation takes on itself. Prompts are the shapes measured in music-harness and this repo.
/// </summary>
public sealed class KakashiHarnessTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "aztest-harness-" + Guid.NewGuid().ToString("n"));
    private readonly string _repo;
    private readonly string _config;
    private DateTimeOffset _now = T0.AddSeconds(30);

    public KakashiHarnessTests()
    {
        _repo = Path.Combine(_tmp, "music.harness");
        _config = Path.Combine(_tmp, "claude-config");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Garden(_repo, "kakashi-harness", "tamer", "lyricist", "genre-scout", "security-guard");
        Expert(_repo, "lyricist", "persona: 작사가", "type: specialist", "description: 가사를 쓰고 검수하는 전문가.");
        Expert(_repo, "security-guard", "persona: Security Guard");
        Expert(_repo, "tamer", "persona: 정원지기");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private static void Garden(string root, string schema, params string[] agents)
    {
        Directory.CreateDirectory(Path.Combine(root, "harness", "agents"));
        File.WriteAllText(Path.Combine(root, "harness", "harness.config.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["$schema"] = schema, ["name"] = "Music Harness", ["version"] = "1.14.0", ["agents"] = agents,
        }));
    }

    private static void Expert(string root, string name, params string[] fields)
        => File.WriteAllLines(Path.Combine(root, "harness", "agents", name + ".md"),
            ["---", $"name: {name}", .. fields, "triggers:", "  - \"가사 점검해\"", "---", "", $"# {name}"]);

    private HarnessRoster Roster() => KakashiHarness.Load(_repo)!;

    // ── the roster ──────────────────────────────────────────────────────────

    [Fact]
    public void A_garden_config_gives_the_experts_with_their_frontmatter()
    {
        var r = Roster();
        Assert.Equal("Music Harness", r.Name);
        Assert.Equal("1.14.0", r.Version);
        Assert.Equal(4, r.Experts.Count);
        var lyricist = r.Expert("lyricist")!;
        Assert.Equal("작사가", lyricist.Persona);
        Assert.Equal("specialist", lyricist.Type);
        Assert.Equal("가사를 쓰고 검수하는 전문가.", lyricist.Description);
        Assert.Equal(new HarnessExpert("genre-scout", null, null, null), r.Expert("genre-scout"));   // no file: still an expert
    }

    [Fact]
    public void Another_harness_schema_is_not_recognised()
    {
        Garden(_repo, "other-harness", "lyricist");
        Assert.Null(KakashiHarness.Load(_repo));
    }

    [Fact]
    public void The_locator_searches_up_to_the_repository_root_and_no_further()
    {
        var deep = Path.Combine(_repo, "songs", "2026-11-19-x");
        Directory.CreateDirectory(deep);
        var locator = new HarnessLocator(() => _now);
        Assert.Equal("Music Harness", locator.Find(deep)?.Name);

        // A repository below the garden is its own: the garden above it is not its harness.
        var nested = Path.Combine(_repo, "vendor", "lib");
        Directory.CreateDirectory(Path.Combine(nested, ".git"));
        Assert.Null(locator.Find(nested));
        Assert.Null(locator.Find(Path.Combine(_tmp, "elsewhere")));
    }

    [Fact]
    public void The_locator_rereads_a_changed_config_after_its_recheck_window()
    {
        var locator = new HarnessLocator(() => _now);
        Assert.Equal(4, locator.Find(_repo)!.Experts.Count);
        Garden(_repo, "kakashi-harness", "tamer", "lyricist");
        File.SetLastWriteTimeUtc(Path.Combine(_repo, "harness", "harness.config.json"), T0.AddHours(1).UtcDateTime);
        Assert.Equal(4, locator.Find(_repo)!.Experts.Count);          // inside the window: kept
        _now = _now.AddMilliseconds(locator.RecheckMs + 1);
        Assert.Equal(2, locator.Find(_repo)!.Experts.Count);
    }

    // ── which expert a sub-agent is ─────────────────────────────────────────

    [Fact]
    public void A_prompt_that_names_one_agent_file_is_that_expert()
    {
        const string prompt = "You are acting as the **lyricist (작사가)** specialist of the Music Harness repo. "
            + "Steps: 1. Read `AGENTS.md`, then your definition `harness/agents/lyricist.md` — follow its review procedure.";
        Assert.Equal("lyricist", KakashiHarness.ExpertOf(Roster(), prompt, "lyricist review of Comma")?.Name);
    }

    [Fact]
    public void A_survey_that_names_several_agent_files_is_no_expert()
    {
        const string prompt = "Map the song-forge flow. Look at harness/agents/genre-scout.md and harness\\agents\\lyricist.md.";
        Assert.Null(KakashiHarness.ExpertOf(Roster(), prompt, "Song-forge and MV flow docs"));
    }

    [Fact]
    public void Several_files_with_an_acting_as_line_pick_the_one_it_acts_as()
    {
        const string prompt = "You are the genre-scout agent. Read harness/agents/genre-scout.md; compare with harness/agents/lyricist.md.";
        Assert.Equal("genre-scout", KakashiHarness.ExpertOf(Roster(), prompt, "x")?.Name);
    }

    [Fact]
    public void Without_a_file_the_acting_as_name_or_the_descriptions_lead_word_decides()
    {
        Assert.Equal("security-guard", KakashiHarness.ExpertOf(Roster(), "You are acting as the security-guard. Check the diff.", "review")?.Name);
        Assert.Equal("lyricist", KakashiHarness.ExpertOf(Roster(), "Review the song.", "lyricist review of Comma")?.Name);
        Assert.Null(KakashiHarness.ExpertOf(Roster(), "You are a general helper.", "Survey voice settings"));
    }

    [Fact]
    public void An_agent_file_of_another_repository_is_not_this_gardens()
    {
        Assert.Equal("lyricist", KakashiHarness.ExpertOfPath(Roster(), Path.Combine(_repo, "harness", "agents", "lyricist.md"))?.Name);
        Assert.Null(KakashiHarness.ExpertOfPath(Roster(), @"C:\other\harness\agents\lyricist.md"));
        Assert.Null(KakashiHarness.ExpertOfPath(Roster(), Path.Combine(_repo, "harness", "agents", "nobody.md")));
    }

    [Fact]
    public void A_subagent_launched_as_an_expert_carries_it()
    {
        var session = new AgentSessionRef("claude", _config, "sess-1", _repo);
        var dir = ClaudeSubagentReader.FolderOf(session);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "agent-a1.meta.json"),
            """{"agentType":"general-purpose","description":"lyricist review of Comma","toolUseId":"t1"}""");
        File.WriteAllText(Path.Combine(dir, "agent-b2.meta.json"),
            """{"agentType":"Explore","description":"Survey harness","toolUseId":"t2"}""");
        var expertPrompt = JsonSerializer.Serialize("You are acting as the **lyricist** specialist. Read harness/agents/lyricist.md.");
        WriteNow(Path.Combine(dir, "agent-a1.jsonl"), $$$"""{"type":"user","timestamp":"{{{At(0)}}}","message":{"role":"user","content":{{{expertPrompt}}}}}""");
        WriteNow(Path.Combine(dir, "agent-b2.jsonl"), $$$"""{"type":"user","timestamp":"{{{At(1)}}}","message":{"role":"user","content":[{"type":"text","text":"Read harness/agents/lyricist.md and harness/agents/tamer.md"}]}}""");

        var list = new ClaudeSubagentReader(() => _now, new HarnessLocator(() => _now)).Read(session);
        Assert.Equal("작사가", list.Single(v => v.Id == "a1").Expert?.Persona);
        Assert.Null(list.Single(v => v.Id == "b2").Expert);
        Assert.All(new ClaudeSubagentReader(() => _now).Read(session), v => Assert.Null(v.Expert));   // no locator: as before
    }

    // ── experts the main conversation works as ──────────────────────────────

    private static string At(int s) => T0.AddSeconds(s).ToString("O");

    private void WriteNow(string path, params string[] lines)
    {
        File.AppendAllText(path, string.Concat(lines.Select(l => l + "\n")));
        File.SetLastWriteTimeUtc(path, _now.UtcDateTime);
    }

    private static string Person(int s, string text) =>
        $$$"""{"type":"user","timestamp":"{{{At(s)}}}","message":{"role":"user","content":{{{JsonSerializer.Serialize(text)}}}}}""";

    private static string Call(int s, string name, object input) =>
        $$$"""{"type":"assistant","timestamp":"{{{At(s)}}}","message":{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"t","name":"{{{name}}}","input":{{{JsonSerializer.Serialize(input)}}}}]}}""";

    private static string Done(int s) =>
        $$$"""{"type":"assistant","timestamp":"{{{At(s)}}}","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"ok"}]}}""";

    private (ClaudeHarnessDutyReader Reader, AgentSessionRef Session, string Path) Duty()
    {
        var session = new AgentSessionRef("claude", _config, "main-1", _repo);
        var path = ClaudeHarnessDutyReader.TranscriptOf(session);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        return (new ClaudeHarnessDutyReader(new HarnessLocator(() => _now), () => _now), session, path);
    }

    [Fact]
    public void Reading_an_experts_definition_puts_it_on_duty_until_the_turn_ends()
    {
        var (reader, session, path) = Duty();
        WriteNow(path, Person(0, "보안 점검해"), Call(2, "Read", new { file_path = Path.Combine(_repo, "harness", "agents", "security-guard.md") }));

        var h = reader.Read(session)!;
        Assert.Equal("Music Harness", h.Name);
        Assert.Equal(4, h.Experts);
        var d = Assert.Single(h.OnDuty);
        Assert.Equal(("security-guard", "Security Guard", "read", "working"), (d.Name, d.Persona, d.Via, d.State));
        Assert.Equal(T0.AddSeconds(2).ToUnixTimeMilliseconds(), d.StartedAt);

        WriteNow(path, Done(9));
        d = Assert.Single(reader.Read(session)!.OnDuty);
        Assert.Equal("done", d.State);
        Assert.Equal(T0.AddSeconds(9).ToUnixTimeMilliseconds(), d.EndedAt);

        _now = T0.AddSeconds(9).AddMilliseconds(reader.KeepFinishedMs + 1);
        Assert.Empty(reader.Read(session)!.OnDuty);
    }

    [Fact]
    public void The_gardener_comes_on_with_the_harness_command_and_the_next_prompt_ends_it()
    {
        var (reader, session, path) = Duty();
        WriteNow(path,
            Person(0, "<command-message>harness-kakashi:harness-creator</command-message>\n<command-name>/harness-kakashi:harness-creator</command-name>"),
            """{"type":"user","isMeta":true,"timestamp":"2026-11-19T12:00:01Z","message":{"role":"user","content":[{"type":"text","text":"Base directory for this skill"}]}}""");
        var d = Assert.Single(reader.Read(session)!.OnDuty);
        Assert.Equal(("tamer", "정원지기", "command", "working"), (d.Name, d.Persona, d.Via, d.State));

        WriteNow(path, Person(5, "고마워"));
        Assert.Equal("done", Assert.Single(reader.Read(session)!.OnDuty).State);
    }

    [Fact]
    public void Tool_results_do_not_end_the_turn_and_editing_an_agent_file_is_tending_not_duty()
    {
        var (reader, session, path) = Duty();
        var lyricist = Path.Combine(_repo, "harness", "agents", "lyricist.md");
        WriteNow(path, Person(0, "가사 점검해"), Call(1, "Read", new { file_path = lyricist }),
            $$$"""{"type":"user","timestamp":"{{{At(2)}}}","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"---"}]}}""",
            Call(3, "Skill", new { skill = "harness-kakashi:harness-creator" }));
        Assert.Equal(["lyricist", "tamer"], reader.Read(session)!.OnDuty.Select(d => d.Name));
        Assert.All(reader.Read(session)!.OnDuty, d => Assert.Equal("working", d.State));

        WriteNow(path, Call(4, "Edit", new { file_path = lyricist, old_string = "a", new_string = "b" }));
        Assert.Equal(["tamer"], reader.Read(session)!.OnDuty.Select(d => d.Name));
    }

    [Fact]
    public void A_shell_read_of_one_agent_file_is_duty_and_a_survey_or_an_edit_is_not()
    {
        // Measured (2026-11-19, this repository): "빌드가 어떻게 되는지 분석해" after /harness-creator — the
        // turn read build-doctor's definition with Bash `cat`, never the Read tool, and showed no expert.
        var (reader, session, path) = Duty();
        WriteNow(path, Person(0, "빌드가 어떻게 되는지 분석해"),
            Call(1, "Bash", new { command = "ls harness/agents harness/engine 2>/dev/null | head -80" }),
            Call(2, "Bash", new { command = "grep -n triggers harness/agents/lyricist.md harness/agents/tamer.md" }));
        Assert.Empty(reader.Read(session)!.OnDuty);   // a survey names no one, or several

        WriteNow(path, Call(3, "Bash", new { command = "cat harness/agents/security-guard.md; echo ===; cat harness/engine/full-review.md 2>/dev/null" }));
        var d = Assert.Single(reader.Read(session)!.OnDuty);
        Assert.Equal(("security-guard", "read", "working"), (d.Name, d.Via, d.State));

        WriteNow(path, Call(4, "PowerShell", new { command = "(Get-Content harness\\agents\\security-guard.md) -replace 'a','b' | Set-Content harness\\agents\\security-guard.md" }));
        Assert.Empty(reader.Read(session)!.OnDuty);   // rewriting the definition is tending
    }

    [Fact]
    public void Writing_an_experts_log_is_proof_of_duty()
    {
        var (reader, session, path) = Duty();
        WriteNow(path, Person(0, "가사 점검해"),
            Call(1, "Write", new { file_path = Path.Combine(_repo, "harness", "logs", "lyricist", "2026-11-19-12-00-review.md"), content = "---" }),
            Call(2, "Write", new { file_path = Path.Combine(_repo, "harness", "logs", "meta", "2026-11-19-12-00-x.md"), content = "---" }),
            Call(3, "Write", new { file_path = Path.Combine(_tmp, "elsewhere", "harness", "logs", "tamer", "x.md"), content = "---" }));
        var d = Assert.Single(reader.Read(session)!.OnDuty);   // "meta" is no expert; another repository's log is not this garden's
        Assert.Equal(("lyricist", "log"), (d.Name, d.Via));
    }

    [Fact]
    public void A_session_outside_a_garden_is_null_and_another_agent_too()
    {
        var (reader, session, _) = Duty();
        Assert.Null(reader.Read(session with { Cwd = Path.Combine(_tmp, "plain") }));
        Assert.Null(reader.Read(session with { Agent = "codex" }));
    }

    [Fact]
    public void A_long_transcript_is_read_from_its_tail()
    {
        var (_, session, path) = Duty();
        var reader = new ClaudeHarnessDutyReader(new HarnessLocator(() => _now), () => _now) { TailBytes = 600 };
        var filler = Person(0, new string('x', 2000));
        WriteNow(path, filler, Person(1, "보안 점검해"), Call(2, "Read", new { file_path = Path.Combine(_repo, "harness", "agents", "security-guard.md") }));

        Assert.Equal("security-guard", Assert.Single(reader.Read(session)!.OnDuty).Name);
    }
}
