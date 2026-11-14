using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Agent.Common.Data.Entities;
using Agent.Common.Services;

namespace Agent.Common.Agents;

/// <summary>
/// A definition that runs an agent CLI through a multi-account wrapper rather than by its own
/// name: <c>claude-sam</c>, <c>codex-work</c> — a shell function or script that points the tool
/// at another profile (<c>CLAUDE_CONFIG_DIR=~/.claude-sam</c>) and runs it with its arguments.
///
/// <para>The wrapper's name is all a definition shows; the folder it points at is set inside the
/// function, so it is never in the tab's environment. The folder is therefore found by the
/// convention those wrappers follow — <c>~/.&lt;command&gt;</c> (<c>~/.claude-sam</c>), else
/// <c>~/.&lt;tool&gt;-profiles/&lt;name&gt;</c> — the same places <see cref="ClaudeLiveStatusSource"/>
/// reads status files from. A wrapper whose folder is not there resolves to no folder rather than
/// to the default profile: an id from another profile does not resume ("No conversation found").</para>
/// </summary>
/// <param name="Tool">The agent the wrapper runs.</param>
/// <param name="Command">The wrapper's command word as written (<c>claude-sam</c>).</param>
/// <param name="Profile">The part after the tool's name (<c>sam</c>).</param>
public sealed record AgentProfileCommand(AgentCliTool Tool, string Command, string Profile)
{
    private static readonly char[] Separators = { ' ', '\t', '"', '\'', ';', '&', '|', '(', ')', '<', '>', '`', '\r', '\n' };

    /// <summary>The wrapper a definition runs, or null — also null for a row that names the tool itself.</summary>
    public static AgentProfileCommand? Find(CliDefinition? definition) =>
        definition is null ? null : Find(definition.ExePath, definition.Arguments);

    /// <summary>
    /// The first command word shaped <c>&lt;tool&gt;-&lt;name&gt;</c> or <c>&lt;tool&gt;_&lt;name&gt;</c>.
    /// Same token-and-stem rule as <see cref="Module.CliToolMention"/>: a folder in a path does not
    /// count, only a command word (<c>C:\bin\claude-sam.cmd</c> → <c>claude-sam</c>).
    /// </summary>
    public static AgentProfileCommand? Find(string? exePath, string? arguments)
    {
        foreach (var word in CommandWords(exePath).Concat(CommandWords(arguments)))
            foreach (var tool in AgentCliTools.All)
            {
                var n = tool.Command.Length;
                if (word.Length > n + 1
                    && word.StartsWith(tool.Command, StringComparison.OrdinalIgnoreCase)
                    && word[n] is '-' or '_')
                    return new AgentProfileCommand(tool, word, word[(n + 1)..]);
            }
        return null;
    }

    /// <summary>
    /// The profile folder the wrapper points the tool at, or null when no conventional folder
    /// exists for it.
    /// </summary>
    public string? ConfigDir(string? homeDir = null)
    {
        homeDir ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(homeDir)) return null;
        string[] candidates =
        {
            Path.Combine(homeDir, "." + Command),
            Path.Combine(homeDir, "." + Tool.Command + "-profiles", Profile),
        };
        foreach (var dir in candidates)
        {
            try { if (Directory.Exists(dir)) return dir; }
            catch { }
        }
        return null;
    }

    private static IEnumerable<string> CommandWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (var token in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = token;
            var slash = name.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) name = name[(slash + 1)..];
            var dot = name.LastIndexOf('.');
            if (dot > 0) name = name[..dot];
            if (name.Length > 0) yield return name;
        }
    }
}

/// <summary>
/// What a terminal definition reopens into: the agent, the command word the resume words follow,
/// and — for a profile wrapper — the store its sessions are in. One answer for the picker's list
/// and for the relaunch, so the two cannot read different profiles.
/// </summary>
/// <param name="Tool">The agent whose sessions are listed and resumed.</param>
/// <param name="Command">The word in the definition the resume words go after (<c>claude</c>, <c>claude-sam</c>).</param>
/// <param name="ConfigDir">The wrapper's profile folder; null for a row that runs the tool by name (the tab's environment decides).</param>
public sealed record AgentResumeTarget(AgentCliTool Tool, string Command, string? ConfigDir)
{
    /// <summary>
    /// The target for <paramref name="definition"/>, or null when there is nothing to resume: a
    /// plain shell, a tool without a session reader, or a wrapper whose profile folder is not found.
    /// </summary>
    public static AgentResumeTarget? Of(CliDefinition? definition, string? homeDir = null)
    {
        if (definition is null) return null;
        if (AgentCliTools.Match(definition) is { } tool)
            return AgentSessionCatalog.Supports(tool) ? new AgentResumeTarget(tool, tool.Command, null) : null;
        if (AgentProfileCommand.Find(definition) is { } wrapper
            && AgentSessionCatalog.Supports(wrapper.Tool)
            && wrapper.ConfigDir(homeDir) is { } dir)
            return new AgentResumeTarget(wrapper.Tool, wrapper.Command, dir);
        return null;
    }

    /// <summary>"Claude", or "Claude (claude-sam)" for a wrapper — which account the picker is showing.</summary>
    public string DisplayName => ConfigDir is null ? Tool.Name : $"{Tool.Name} ({Command})";

    /// <summary>The sessions this target can resume in <paramref name="cwd"/>, newest first.</summary>
    /// <param name="env">The tab's environment — consulted only for a row that runs the tool by name.</param>
    public IReadOnlyList<AgentSessionInfo> ListSessions(string cwd, IReadOnlyDictionary<string, string>? env = null,
        int max = 30, string? homeDir = null) =>
        ConfigDir is null
            ? AgentSessionCatalog.List(Tool, cwd, env, max, homeDir)
            : AgentSessionCatalog.ListIn(Tool, cwd, ConfigDir, max);

    /// <summary>A copy of <paramref name="definition"/> that reopens <paramref name="sessionId"/>, or null.</summary>
    public CliDefinition? WithResume(CliDefinition definition, string sessionId) =>
        AgentResumeLaunch.WithResume(definition, Tool, sessionId, Command);
}
