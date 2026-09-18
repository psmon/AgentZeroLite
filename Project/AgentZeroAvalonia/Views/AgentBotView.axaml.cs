using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Agent.Common.Agents;
using Agent.Common.Services;
using AgentZeroAvalonia.ViewModels;

namespace AgentZeroAvalonia.Views;

/// <summary>
/// The AgentBot pane (M0037, extended in M0041). Enter sends and Shift+Enter starts a new
/// line; Shift+Tab cycles the mode; Esc cancels a running AI turn. In KEY mode the input box
/// becomes a key relay — arrows, Esc, Tab, Enter, Backspace, every Ctrl+letter and typed
/// characters go straight to the terminal, as in the WPF host.
/// </summary>
public partial class AgentBotView : UserControl
{
    private AgentBotViewModel? _vm;

    public AgentBotView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as AgentBotViewModel);

        // Without this, detaching and re-embedding the pane leaves one more live ItemAdded
        // handler on the same view model each time, and the transcript scrolls N times per
        // message. Attach() only unsubscribes when the view model itself changes.
        DetachedFromVisualTree += (_, _) => Attach(null);

        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        InputBox.AddHandler(TextInputEvent, OnInputTextInput, RoutingStrategies.Tunnel);
        InputBox.AddHandler(TextBox.PastingFromClipboardEvent, OnPasting, RoutingStrategies.Tunnel);
    }

    private void Attach(AgentBotViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm is not null) _vm.ItemAdded -= ScrollToEnd;
        _vm = vm;
        if (_vm is null) return;
        _vm.ItemAdded += ScrollToEnd;
    }

    private void ScrollToEnd()
    {
        Dispatcher.UIThread.Post(() => Transcript.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>Drag the grip up to give the composer more room (the WPF resize handle).</summary>
    private void OnInputResize(object? sender, VectorEventArgs e)
    {
        if (_vm is null) return;
        _vm.InputMaxHeight = Math.Clamp(_vm.InputMaxHeight - e.Vector.Y, 40, 400);
    }

    /// <summary>
    /// A large paste is held back as a chip rather than dumped into the composer. The clipboard
    /// read is async, so the event is cancelled first and the text collected afterwards.
    /// </summary>
    private async void OnPasting(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        var caret = InputBox.CaretIndex;
        string? text;
        try
        {
            text = await clipboard.TryGetTextAsync();
        }
        catch
        {
            return;   // no clipboard access — let the default paste stand
        }

        if (!ClipboardAttachment.ShouldAttach(text)) return;

        // The default paste has already landed by now; take it back out and hold it instead.
        _vm.AttachClipboard(text!, caret);
        var current = InputBox.Text ?? "";
        if (current.Contains(text!)) current = current.Replace(text!, "");
        InputBox.Text = current;
        InputBox.CaretIndex = Math.Min(caret, current.Length);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;

        // Shift+Tab cycles the mode, in every mode — checked before KEY mode claims Tab.
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _vm.CycleMode();
            e.Handled = true;
            return;
        }

        // Esc cancels a running AI turn rather than reaching the terminal.
        if (e.Key == Key.Escape && _vm.AiBusy)
        {
            _vm.Cancel();
            e.Handled = true;
            return;
        }

        if (_vm.IsKeyMode)
        {
            if (e.Key == Key.Escape)
            {
                _vm.SendMiniKey("esc");   // ESC, a beat, then Ctrl+C — the WPF sequence
                e.Handled = true;
                return;
            }

            TerminalControl? control = e.Key switch
            {
                Key.Up => TerminalControl.UpArrow,
                Key.Down => TerminalControl.DownArrow,
                Key.Left => TerminalControl.LeftArrow,
                Key.Right => TerminalControl.RightArrow,
                Key.Tab => TerminalControl.Tab,
                Key.Enter => TerminalControl.Enter,
                Key.Back => TerminalControl.Backspace,
                Key.Delete => TerminalControl.Delete,
                Key.Home => TerminalControl.Home,
                Key.End => TerminalControl.End,
                Key.PageUp => TerminalControl.PageUp,
                Key.PageDown => TerminalControl.PageDown,
                Key.Space => TerminalControl.Space,
                _ => null,
            };
            if (control is { } c)
            {
                _vm.SendControl(c);
                e.Handled = true;
                return;
            }

            // Every Ctrl+letter, not just Ctrl+C — 0x01..0x1A.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key >= Key.A && e.Key <= Key.Z)
            {
                var letter = (char)('A' + (e.Key - Key.A));
                if (KeyChordTranslator.ControlChar(letter) is { } ctrl)
                {
                    _vm.SendRaw(ctrl.ToString());
                    e.Handled = true;
                }
                return;
            }

            // Printable characters fall through to the text-input handler below.
            return;
        }

        // Enter sends; Shift+Enter is a new line.
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _vm.Send();
            e.Handled = true;
        }
    }

    private void OnInputTextInput(object? sender, TextInputEventArgs e)
    {
        if (_vm is null || !_vm.IsKeyMode || string.IsNullOrEmpty(e.Text)) return;
        _vm.SendRaw(e.Text);
        e.Handled = true;
    }
}
