using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Agent.Common;
using AgentZeroWpf.Services.Voice;
using Brush = System.Windows.Media.Brush;

namespace AgentZeroWpf.UI.APP;

/// <summary>
/// Standalone popup that hosts the developer test panels — currently just
/// the Virtual Voice injector. Built with a TabControl so additional test
/// panels (e.g. virtual keyboard, IPC ping) drop in as new tabs without
/// touching AgentBotWindow.
///
/// Lifecycle: owned by AgentBotWindow. <c>AgentBotWindow.Voice</c> creates
/// a fresh instance per toggle click and disposes the
/// <see cref="VirtualVoiceInjector"/> in <see cref="OnClosed"/>. There's no
/// caching — opening twice closes the previous; minor cost for the cleaner
/// state model.
/// </summary>
public partial class TestToolsWindow : Window
{
    private VirtualVoiceInjector? _injector;

    public TestToolsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => txtVoiceInput.Focus();
    }

    private void OnVoiceInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyboardDevice.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = RunSpeakAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnVoiceSpeak(object sender, RoutedEventArgs e) => _ = RunSpeakAsync();

    private async Task RunSpeakAsync()
    {
        var text = txtVoiceInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            SetStatus("Type something first.", isError: false);
            return;
        }

        if (_injector is null)
        {
            _injector = new VirtualVoiceInjector();
            _injector.Started += () => SetStatus("▶ playing through speaker…", isError: false);
            _injector.Stopped += () => SetStatus("done — if mic is ON, AskBot should have heard it.", isError: false);
            _injector.Errored += ex => SetStatus($"✗ {ex.Message}", isError: true);
        }

        SetStatus("synthesising…", isError: false);
        try
        {
            await _injector.SpeakAsync(text);
        }
        catch (Exception ex)
        {
            // Errored event already fired by the injector; the catch keeps the
            // task from observing as unhandled. Log as a backup.
            AppLogger.Log($"[TestTools] SpeakAsync threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void SetStatus(string text, bool isError)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<string, bool>(SetStatus), DispatcherPriority.Normal, text, isError);
            return;
        }
        if (txtVoiceStatus is null) return;
        txtVoiceStatus.Text = text;
        txtVoiceStatus.Foreground = isError
            ? Brushes.OrangeRed
            : (Brush)FindResource("TextDim");
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _injector?.Dispose(); } catch { }
        _injector = null;
        base.OnClosed(e);
    }
}
