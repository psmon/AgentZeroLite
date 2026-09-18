namespace Agent.Common.Agents;

/// <summary>
/// A large paste held back from the input box (M0041). Pasting a few thousand characters
/// into a one-line composer makes it unusable, so the bot keeps the text aside, shows a
/// chip, and splices it back at the caret when the message is sent. Ported from the WPF
/// host so both hosts use the same threshold and the same display form.
/// </summary>
/// <param name="Text">The full pasted text.</param>
/// <param name="CaretIndex">Where in the typed input the user meant to paste it.</param>
public sealed record ClipboardAttachment(string Text, int CaretIndex)
{
    /// <summary>Pastes longer than this become an attachment instead of inline text.</summary>
    public const int Threshold = 200;

    /// <summary>How much of the text the preview line shows.</summary>
    public const int PreviewLength = 300;

    /// <summary>True when a paste of this length should be held back.</summary>
    public static bool ShouldAttach(string? pasted) => pasted is not null && pasted.Length > Threshold;

    /// <summary>The chip caption, e.g. <c>[clipboard 4096 chars]</c>.</summary>
    public string Tag => $"[clipboard {Text.Length} chars]";

    /// <summary>The first <see cref="PreviewLength"/> characters, ellipsised.</summary>
    public string Preview => Text.Length > PreviewLength ? Text[..PreviewLength] + "..." : Text;

    /// <summary>
    /// What to send to the terminal and what to show in the transcript. The attachment is
    /// spliced at the caret; the transcript shows the chip in its place so the bubble stays
    /// readable. A caret past the end of the typed text is clamped — the input can shrink
    /// between the paste and the send.
    /// </summary>
    public static (string Send, string Display) Compose(string? typed, ClipboardAttachment? attachment)
    {
        typed ??= "";

        if (attachment is null)
        {
            var trimmed = typed.Trim();
            return (trimmed, trimmed);
        }

        if (typed.Length == 0)
            return (attachment.Text, $"📋{attachment.Tag}");

        var pos = Math.Clamp(attachment.CaretIndex, 0, typed.Length);
        var before = typed[..pos];
        var after = typed[pos..];
        return (before + attachment.Text + after, $"{before}📋{attachment.Tag}{after}");
    }
}
