using Agent.Common.Platform;
using System;
using System.IO;
using System.Text.Json;
using Agent.Common.Data.Entities;
using Agent.Common.Module;
using Agent.Common.Services;

namespace Agent.Common.Agents;

/// <summary>
/// Turns a terminal definition into "the same terminal, reopened into session X". The
/// built-in rows start the agent inside a shell (<c>powershell -NoExit -Command claude</c>,
/// <c>zsh -l -c "codex; exec zsh -l"</c>), so the resume words go right after the agent's
/// command word, wherever it is — not at the end, where a POSIX row's <c>; exec zsh</c> and
/// a person's own flags live. For Codex the order matters: <c>resume</c> is a subcommand.
/// </summary>
public static class AgentResumeLaunch
{
    /// <summary>
    /// A copy of <paramref name="definition"/> that resumes <paramref name="sessionId"/>, or
    /// null when the id is unsafe, the tool has no resume form, or the definition does not
    /// name the tool where a resume could be put.
    /// </summary>
    /// <param name="command">The word the resume words follow, when the definition runs the tool
    /// through a wrapper (<c>claude-sam</c>); the tool's own command otherwise. The wrapper must
    /// pass its arguments on (<c>&amp; claude @args</c>).</param>
    public static CliDefinition? WithResume(CliDefinition definition, AgentCliTool tool, string sessionId, string? command = null)
    {
        var arguments = InsertResume(definition.ExePath, definition.Arguments, tool, sessionId, command);
        if (arguments is null) return null;
        return new CliDefinition
        {
            Id = definition.Id,
            Name = definition.Name,
            ExePath = definition.ExePath,
            Arguments = arguments,
            IsBuiltIn = definition.IsBuiltIn,
            ReducedMotion = definition.ReducedMotion,
            SortOrder = definition.SortOrder,
            IsRemote = definition.IsRemote,
            SshHost = definition.SshHost,
            SshUser = definition.SshUser,
            SshAuthMethod = definition.SshAuthMethod,
            SshKeyPath = definition.SshKeyPath,
            EncryptedPassword = definition.EncryptedPassword,
        };
    }

    /// <summary>The definition's argument string with the resume words added, or null (see <see cref="WithResume"/>).</summary>
    public static string? InsertResume(string? exePath, string? arguments, AgentCliTool tool, string sessionId,
        string? command = null)
    {
        if (!AgentResumeCatalog.IsSafeId(sessionId)) return null;
        var spec = AgentResumeCatalog.Lookup(tool.Command);
        if (spec is null) return null;
        var words = spec.ArgTemplate.Replace("{id}", sessionId);
        var word = string.IsNullOrWhiteSpace(command) ? tool.Command : command;

        // The definition launches the agent directly: the resume words lead its arguments.
        if (CliToolMention.Mentions(exePath, word))
            return string.IsNullOrWhiteSpace(arguments) ? words : words + " " + arguments.TrimStart();

        var end = CliToolMention.FindCommandEnd(arguments, word);
        if (end < 0) return null;
        return arguments![..end] + " " + words + arguments[end..];
    }
}

/// <summary>
/// Whether a newly started Claude / Codex tab asks which earlier session to reopen. Per
/// tool, because "never ask" is usually said about one agent, not both. A side-car JSON of
/// its own (not <see cref="TerminalSettings"/>): the appearance page rebuilds that object
/// from its fields on save, which would quietly reset these.
/// </summary>
public sealed class AgentResumeSettings
{
    public bool AskClaude { get; set; } = true;
    public bool AskCodex { get; set; } = true;

    public bool Asks(AgentCliTool tool) =>
        ReferenceEquals(tool, AgentCliTools.Claude) ? AskClaude
        : ReferenceEquals(tool, AgentCliTools.Codex) && AskCodex;

    public void SetAsks(AgentCliTool tool, bool ask)
    {
        if (ReferenceEquals(tool, AgentCliTools.Claude)) AskClaude = ask;
        else if (ReferenceEquals(tool, AgentCliTools.Codex)) AskCodex = ask;
    }
}

/// <summary><c>%LOCALAPPDATA%\AgentZeroLite\agent-resume.json</c> — same shape as <see cref="TerminalSettingsStore"/>.</summary>
public static class AgentResumeSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Overridable for tests.</summary>
    public static string FilePath { get; set; } = Path.Combine(
        AppPaths.DataRoot,
        "agent-resume.json");

    public static AgentResumeSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AgentResumeSettings();
            return JsonSerializer.Deserialize<AgentResumeSettings>(File.ReadAllText(FilePath)) ?? new AgentResumeSettings();
        }
        catch
        {
            return new AgentResumeSettings();
        }
    }

    public static void Save(AgentResumeSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
    }
}
