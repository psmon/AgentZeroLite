using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Agent.Common.Services;
using AgentZeroAvalonia.ViewModels;

namespace AgentZeroAvalonia.Views;

/// <summary>
/// The AgentBot pane (M0037). Enter sends; in KEY mode the input box becomes a key relay
/// (arrows, Esc, Tab, Enter, Backspace, Ctrl+C go straight to the terminal, typed
/// characters too), as in the WPF host.
/// </summary>
public partial class AgentBotView : UserControl
{
    private AgentBotViewModel? _vm;

    public AgentBotView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as AgentBotViewModel);
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        InputBox.AddHandler(TextInputEvent, OnInputTextInput, RoutingStrategies.Tunnel);
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

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.IsKeyMode)
        {
            TerminalControl? control = e.Key switch
            {
                Key.Escape => TerminalControl.Escape,
                Key.Up => TerminalControl.UpArrow,
                Key.Down => TerminalControl.DownArrow,
                Key.Left => TerminalControl.LeftArrow,
                Key.Right => TerminalControl.RightArrow,
                Key.Tab => e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? TerminalControl.BackTab : TerminalControl.Tab,
                Key.Enter => TerminalControl.Enter,
                Key.Back => TerminalControl.Backspace,
                Key.Delete => TerminalControl.Delete,
                Key.Home => TerminalControl.Home,
                Key.End => TerminalControl.End,
                Key.PageUp => TerminalControl.PageUp,
                Key.PageDown => TerminalControl.PageDown,
                Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control) => TerminalControl.Interrupt,
                _ => null,
            };
            if (control is { } c)
            {
                _vm.SendControl(c);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Space)
            {
                _vm.SendControl(TerminalControl.Space);
                e.Handled = true;
            }
            return;
        }

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
