namespace Agent.Common.Module;

/// <summary>
/// Turns "I don't want the animations" into whatever the agent CLI in this tab
/// actually calls that.
///
/// <para>Some TUIs draw decoration behind their input — Codex shimmers the prompt
/// on certain models — and on a terminal that repaints continuously it is
/// distracting. Each CLI spells the off switch differently and most do not have
/// one, so a checkbox that just appended a guess would produce tabs that refuse to
/// start. This resolves the switch by CLI, and returns nothing when there isn't
/// one, so the option is safe to tick on any definition.</para>
///
/// <para>Adding a CLI here means finding its documented flag, not inventing one:
/// an argument a program does not recognise is usually fatal at launch.</para>
/// </summary>
public static class ReducedMotionArguments
{
    /// <summary>
    /// Codex's reduced-motion setting — the same one it turns on by itself when it
    /// detects a screen reader. Note it is known to stop the "Working" timer
    /// updating (openai/codex#45564); the work still runs.
    /// </summary>
    public const string CodexFlag = "-c tui.animations=false";

    /// <summary>
    /// The extra arguments to append, or an empty string when this CLI has no
    /// reduced-motion switch we know of.
    /// </summary>
    /// <param name="exePath">The definition's executable — often a shell.</param>
    /// <param name="arguments">
    /// Its arguments, which is where the agent CLI usually appears: the shipped
    /// definitions launch through <c>powershell -NoExit -Command claude</c> rather
    /// than invoking the agent directly.
    /// </param>
    public static string Resolve(string? exePath, string? arguments)
    {
        return CliToolMention.Mentions(exePath, arguments, "codex") ? CodexFlag : "";
    }

    /// <summary>
    /// Append <see cref="Resolve"/>'s result to an existing argument string.
    /// Returns the original when there is nothing to add, so callers can apply it
    /// unconditionally.
    /// </summary>
    public static string? Append(string? arguments, string? exePath, bool enabled)
    {
        if (!enabled) return arguments;

        var extra = Resolve(exePath, arguments);
        if (extra.Length == 0) return arguments;
        if (string.IsNullOrWhiteSpace(arguments)) return extra;

        // Already there — ticking the box twice, or a definition that spelled it out
        // by hand, must not produce it twice.
        return arguments.Contains(extra, StringComparison.OrdinalIgnoreCase)
            ? arguments
            : arguments + " " + extra;
    }
}
