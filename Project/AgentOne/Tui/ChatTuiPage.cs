using R3;
using Termina.Components.Streaming;
using Termina.Extensions;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace AgentOne.Tui;

/// <summary>
/// The chat window: transcript above, one input line at the bottom, a header
/// that says which mode you are in. The transcript is a StreamingTextNode over
/// a buffer the page also holds, so the page can tell whether the person has
/// scrolled away from the live end and leave them there while new text lands.
/// </summary>
public sealed class ChatTuiPage : ReactivePage<ChatTuiViewModel>
{
    private static readonly Color UserColor = Color.BrightCyan;
    private static readonly Color AnswerColor = Color.White;
    private static readonly Color NoteColor = Color.BrightBlack;
    private static readonly Color AlertColor = Color.BrightYellow;

    /// <summary>Columns the scrollbar takes on the right of the transcript.</summary>
    private const int ScrollbarColumns = 1;

    /// <summary>Lines one wheel tick moves — what terminals themselves do.</summary>
    private const int WheelLines = 3;

    private readonly SoftWrap _wrap = new();
    private PersistedStreamBuffer _buffer = null!;
    private StreamingTextNode _transcript = null!;

    protected override void OnBound()
    {
        // The buffer's own auto-scroll follows new text only while the view is
        // at the bottom — exactly what a chat wants. Calling ScrollToBottom on
        // every append, as the first version did, yanked the reader back down
        // while they were trying to read what had scrolled past.
        _buffer = new PersistedStreamBuffer { AutoScroll = true };
        _transcript = new StreamingTextNode(_buffer).WithScrollbar();

        ViewModel.Lines.Subscribe(Append).AddTo(Subscriptions);

        ViewModel.Scroll.Subscribe(request =>
        {
            // ScrollUp's second argument is the wrap WIDTH, not the height: the
            // buffer needs it to count wrapped lines. Passing the height there
            // is why PageUp did nothing in the first version.
            var width = WrapWidth();
            switch (request)
            {
                case ScrollRequest.Up: _transcript.ScrollUp(PageLines(), width); break;
                case ScrollRequest.Down: _transcript.ScrollDown(PageLines()); break;
                case ScrollRequest.WheelUp: _transcript.ScrollUp(WheelLines, width); break;
                case ScrollRequest.WheelDown: _transcript.ScrollDown(WheelLines); break;
                case ScrollRequest.Bottom: _transcript.ScrollToBottom(); break;
            }
            ReportScroll();
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
        // Everything is folded to the window width first — see SoftWrap for
        // why a long line must never reach the buffer.
        var width = WrapWidth();
        switch (line.Kind)
        {
            case LineKind.User:
                Line("");
                Line("› " + line.Text, UserColor);
                break;
            case LineKind.AnswerStart:
                _transcript.Append(_wrap.Fold("◆ ", width), AnswerColor);
                break;
            case LineKind.Delta:
                _transcript.Append(_wrap.Fold(line.Text, width), AnswerColor);
                break;
            case LineKind.AnswerEnd:
                Line("");
                break;
            case LineKind.Note:
                Line("  " + line.Text, NoteColor);
                break;
            case LineKind.Alert:
                Line("  " + line.Text, AlertColor);
                break;
        }

        ReportScroll();

        void Line(string text, Color? colour = null)
        {
            var folded = _wrap.Fold(text, width);
            if (colour is { } c) _transcript.AppendLine(folded, c); else _transcript.AppendLine(folded);
            _wrap.NewLine();
        }
    }

    /// <summary>Tells the view model whether the reader is away from the live end, for the header.</summary>
    private void ReportScroll() =>
        ViewModel.SetScrolled(_buffer.IsScrolledUp, _buffer.ScrollOffset);

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
        var right = model.Busy ? "working…" : model.AwaitingPerson ? "approve? y/n" : $"turn {model.Turns}";

        // The live numbers a person glances at; F2 prints the whole block.
        var stats = ViewModel.Session.Stats();
        var counters = $" · ctx ~{stats.EstimatedTokens / 1000.0:0.0}k · jev {stats.Counters.JevCalls}";

        // Say when the reader has scrolled away from the live end, and how to get back.
        var scrolled = model.ScrolledUp
            ? $"   ↑ {model.ScrollOffset} lines above the end · Ctrl+End to follow"
            : "";

        var title = model.Title.Length > 0 ? $" · {model.Title}" : "";

        return new TextNode($" {mode}  agent-one · {right}{counters}{title}{scrolled}").WithForeground(colour).NoWrap();
    }

    private ILayoutNode InputLine()
    {
        var model = ViewModel.Model;
        var prompt = model.AwaitingPerson ? "approve (y/n) › " : model.Smart ? "smart › " : "› ";

        // The cursor is drawn as a block; the shell's own cursor is hidden by
        // the full-screen mode. A pasted paragraph is shown as a window around
        // the cursor rather than wrapped into the transcript.
        var shown = InputViewport.Render(model.Input, model.Cursor, WrapWidth() + ScrollbarColumns - prompt.Length);

        return new TextNode(prompt + shown)
            .WithForeground(model.Busy ? NoteColor : Color.BrightWhite)
            .NoWrap();
    }

    private static int WrapWidth()
    {
        try { return Math.Max(20, Console.WindowWidth - ScrollbarColumns); }
        catch (IOException) { return 80 - ScrollbarColumns; }
    }

    /// <summary>A page is the transcript's height minus the three fixed rows, less one for context.</summary>
    private static int PageLines()
    {
        try { return Math.Max(3, Console.WindowHeight - 3 - 1); }
        catch (IOException) { return 20; }
    }
}
