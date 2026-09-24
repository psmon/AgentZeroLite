namespace Agent.Common.Module;

/// <summary>
/// "Does this definition name <c>codex</c> as a command?" — one answer, two callers.
///
/// <para>A <see cref="Data.Entities.CliDefinition"/> rarely invokes an agent CLI
/// directly: the shipped rows launch <c>powershell -NoExit -Command claude</c>, so the
/// tool's name lives in the arguments, next to a shell, flags and sometimes a path.
/// Both <see cref="ReducedMotionArguments"/> (which flag to append) and
/// <see cref="Services.AgentCliTools"/> (is it installed, and how would we install it)
/// need to recognise the tool in that text, and they must agree — a definition offered
/// an "Install Codex" button while the reduced-motion flag went elsewhere would be
/// two different readings of one row.</para>
///
/// <para>The rule is token-and-stem, not substring: a path like
/// <c>C:\codex-notes\run.cmd</c> or a folder named after the tool does not count,
/// because appending a flag to the wrong program is usually fatal at launch.</para>
/// </summary>
public static class CliToolMention
{
    /// <summary>
    /// What ends a command word. Whitespace and quotes are the obvious ones; the shell
    /// metacharacters matter because the POSIX built-ins are written
    /// <c>zsh -l -c "codex; exec zsh -l"</c> — with only whitespace as a separator the
    /// token is <c>codex;</c>, which matches nothing, and the macOS rows for both agents
    /// were invisible to this check.
    /// </summary>
    private static readonly char[] Separators =
        { ' ', '\t', '"', '\'', ';', '&', '|', '(', ')', '<', '>', '`', '\r', '\n' };

    /// <summary>Whether <paramref name="text"/> names <paramref name="tool"/> as a command.</summary>
    public static bool Mentions(string? text, string tool)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (var token in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = token;

            // Strip a path, then an extension: "C:\tools\codex.cmd" → "codex".
            var slash = name.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) name = name[(slash + 1)..];
            var dot = name.LastIndexOf('.');
            if (dot > 0) name = name[..dot];

            if (string.Equals(name, tool, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Whether either half of a definition names the tool.</summary>
    public static bool Mentions(string? exePath, string? arguments, string tool) =>
        Mentions(exePath, tool) || Mentions(arguments, tool);
}
