using System.Text.RegularExpressions;

namespace Agent.Common.Actors;

/// <summary>
/// Who a <c>bot-chat</c> message is from and what it says — the reading both hosts use.
///
/// <para>Two envelopes exist: <c>DONE(text) --from Name</c>, which the handshake teaches, and
/// the older <c>DONE(Name, text)</c> with no <c>--from</c>. Reading every message as the older
/// one first was wrong the moment two agents talked to each other. Measured (2026-11-24): Hermes
/// answered Codex with <c>bot-chat "DONE(Codex, 가장 현실적인 첫걸음은 …)" --from Hermes</c> — a
/// reply ADDRESSED to Codex — and the first comma made "Codex" the sender. The bot then relayed
/// Hermes' own words back to Hermes as "[from Codex]", and Hermes went on answering itself. The
/// same reading turned <c>DONE(네, 맞아요)</c> into a message from a peer called "네".</para>
///
/// <para>So an explicit <c>--from</c> is the sender, and the whole inner text is the message.
/// The name inside the envelope counts only when the caller gave no <c>--from</c> (the CLI then
/// sends <see cref="DefaultFrom"/>).</para>
/// </summary>
public static partial class PeerEnvelope
{
    /// <summary>What the CLI sends as <c>from</c> when <c>--from</c> was not given.</summary>
    public const string DefaultFrom = "CLI";

    /// <param name="Peer">The sender's tab name.</param>
    /// <param name="Text">The message, without the envelope.</param>
    /// <param name="IsDone">Whether it came in a <c>DONE(...)</c> envelope.</param>
    public readonly record struct Parsed(string Peer, string Text, bool IsDone);

    public static Parsed Parse(string? from, string? message)
    {
        from = (from ?? "").Trim();
        message = (message ?? "").Trim();
        var named = from.Length > 0 && !string.Equals(from, DefaultFrom, StringComparison.OrdinalIgnoreCase);

        var done = Done().Match(message);
        if (!done.Success) return new Parsed(named ? from : DefaultFrom, message, false);

        var inner = done.Groups["inner"].Value.Trim();
        if (named) return new Parsed(from, inner, true);

        // No --from: the legacy "DONE(Name, text)" is the only place a name can be.
        var comma = inner.IndexOf(',');
        if (comma > 0 && comma < inner.Length - 1)
        {
            var name = inner[..comma].Trim();
            var text = inner[(comma + 1)..].Trim();
            if (name.Length > 0 && text.Length > 0 && !name.Any(char.IsWhiteSpace))
                return new Parsed(name, text, true);
        }
        return new Parsed(DefaultFrom, inner, true);
    }

    [GeneratedRegex(@"^DONE\((?<inner>.+)\)$", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Done();
}
