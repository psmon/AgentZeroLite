using R3;
using Termina.Extensions;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace AgentOne.Tui;

/// <summary>
/// The chat window: transcript above, one input line at the bottom, a header
/// that says which mode you are in. The transcript is a StreamingTextNode the
/// page owns — Termina's own answer to "text that keeps arriving" — and the
/// page only ever appends to it.
/// </summary>
public sealed class ChatTuiPage : ReactivePage<ChatTuiViewModel>
{
    private static readonly Color UserColor = Color.BrightCyan;
    private static readonly Color AnswerColor = Color.White;
    private static readonly Color NoteColor = Color.BrightBlack;
    private static readonly Color AlertColor = Color.BrightYellow;

    private StreamingTextNode _transcript = null!;

    protected override void OnBound()
    {
        _transcript = StreamingTextNode.Create().WithScrollbar();

        ViewModel.Lines.Subscribe(Append).AddTo(Subscriptions);
        ViewModel.Scroll.Subscribe(delta =>
        {
            var viewport = Math.Max(5, SafeWindowHeight() - 5);
            if (delta < 0) _transcript.ScrollUp(-delta, viewport);
            else _transcript.ScrollDown(delta);
        }).AddTo(Subscriptions);

        Append(new TranscriptLine(LineKind.Note,
            $"agent-one chat · {ViewModel.Session.ProviderName} · {ViewModel.Session.Model}"));
        Append(new TranscriptLine(LineKind.Note, $"tools: {ViewModel.Session.ToolScope}"));
        if (ViewModel.Session.LogPath is { } log)
            Append(new TranscriptLine(LineKind.Note, $"session: {log}"));
        Append(new TranscriptLine(LineKind.Note, ""));
    }

    private void Append(TranscriptLine line)
    {
        switch (line.Kind)
        {
            case LineKind.User:
                _transcript.AppendLine("");
                _transcript.AppendLine("› " + line.Text, UserColor);
                break;
            case LineKind.AnswerStart:
                _transcript.Append("◆ ", AnswerColor);
                break;
            case LineKind.Delta:
                _transcript.Append(line.Text, AnswerColor);
                break;
            case LineKind.AnswerEnd:
                _transcript.AppendLine("");
                break;
            case LineKind.Note:
                _transcript.AppendLine("  " + line.Text, NoteColor);
                break;
            case LineKind.Alert:
                _transcript.AppendLine("  " + line.Text, AlertColor);
                break;
        }

        _transcript.ScrollToBottom();
    }

    public override ILayoutNode BuildLayout()
    {
        return Layouts.Vertical()
            .WithChild(
                ViewModel.Revision
                    .Select<int, ILayoutNode>(_ => Header())
                    .AsLayout()
                    .Height(1))
            .WithChild(_transcript.Fill())
            .WithChild(
                ViewModel.Revision
                    .Select<int, ILayoutNode>(_ => new TextNode(" " + ViewModel.Model.Status).WithForeground(NoteColor).NoWrap())
                    .AsLayout()
                    .Height(1))
            .WithChild(
                ViewModel.Revision
                    .Select<int, ILayoutNode>(_ => InputLine())
                    .AsLayout()
                    .Height(1));
    }

    private ILayoutNode Header()
    {
        var model = ViewModel.Model;
        var mode = model.Smart ? "[smart]" : "[basic]";
        var colour = model.Smart ? Color.BrightGreen : Color.BrightCyan;
        var right = model.Busy ? "working…" : model.AwaitingPerson ? "waiting for you" : $"turn {model.Turns}";

        return new TextNode($" {mode}  agent-one · {right}").WithForeground(colour).NoWrap();
    }

    private ILayoutNode InputLine()
    {
        var model = ViewModel.Model;
        var prompt = model.AwaitingPerson ? "answer › " : model.Smart ? "smart › " : "› ";

        // The cursor is drawn as a block; the shell's own cursor is hidden by
        // the full-screen mode.
        var text = model.Input;
        var shown = text[..model.Cursor] + "▌" + text[model.Cursor..];

        return new TextNode(prompt + shown)
            .WithForeground(model.Busy ? NoteColor : Color.BrightWhite)
            .NoWrap();
    }

    private static int SafeWindowHeight()
    {
        try { return Console.WindowHeight; }
        catch (IOException) { return 24; }
    }
}
