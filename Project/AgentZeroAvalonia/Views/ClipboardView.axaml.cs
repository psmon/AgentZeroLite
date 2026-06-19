using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AgentZeroAvalonia.Models;
using AgentZeroAvalonia.ViewModels;

namespace AgentZeroAvalonia.Views;

public partial class ClipboardView : UserControl
{
    private readonly DispatcherTimer _timer;

    public ClipboardView()
    {
        InitializeComponent();
        // Avalonia엔 클립보드 변경 이벤트가 없어 1.5초 폴링.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += async (_, _) => await PollAsync();
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    private async System.Threading.Tasks.Task PollAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || DataContext is not ClipboardViewModel vm) return;
        try
        {
            var text = await clipboard.GetTextAsync();
            vm.Observe(text);
        }
        catch { /* 클립보드 접근 실패는 무시 */ }
    }

    private async void OnCopyBack(object? sender, TappedEventArgs e)
    {
        if (HistoryList.SelectedItem is not ClipboardEntry entry) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try { await clipboard.SetTextAsync(entry.Text); } catch { /* ignore */ }
    }
}
