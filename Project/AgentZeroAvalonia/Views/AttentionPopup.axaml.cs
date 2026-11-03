using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Agent.Common.Agents;

namespace AgentZeroAvalonia.Views;

/// <summary>One row of the popup: which terminal, and why it wants the user.</summary>
public sealed class AttentionPopupItem
{
    public AttentionPopupItem(AttentionAlert alert, string workspace)
    {
        Alert = alert;
        Headline = string.IsNullOrEmpty(workspace) ? alert.Title : $"{workspace} / {alert.Title}";
        Detail = alert.Kind switch
        {
            AttentionKind.Blocked => "blocked — waiting for input or approval",
            AttentionKind.Notify => alert.Message ?? "sent a notification",
            _ => "done — finished, not looked at yet",
        };
        // The sidebar's dot colours (ActiveSessionItem.Visual); a program's own notification is yellow.
        Brush = new SolidColorBrush(Color.FromUInt32(alert.Kind switch
        {
            AttentionKind.Blocked => 0xFFE06C75,
            AttentionKind.Notify => 0xFFE5C07B,
            _ => 0xFF61AFEF,
        }));
    }

    public AttentionAlert Alert { get; }
    public string Headline { get; }
    public string Detail { get; }
    public IBrush Brush { get; }
}

/// <summary>
/// The corner popup for "an agent needs you". One window collects the alerts that arrive while
/// it is up (newest first, at most <see cref="MaxRows"/>); each new alert restarts the timer.
/// Clicking a row goes to that terminal; it never takes the focus by itself.
/// </summary>
public partial class AttentionPopup : Window
{
    public const int MaxRows = 4;

    private readonly DispatcherTimer _timer = new();
    private Window? _anchor;

    public AttentionPopup()
    {
        InitializeComponent();
        DataContext = this;
        _timer.Tick += (_, _) => Close();
        SizeChanged += (_, _) => PlaceInCorner();
        Closed += (_, _) => _timer.Stop();
    }

    public ObservableCollection<AttentionPopupItem> Items { get; } = new();

    /// <summary>Raised when the user clicks a row.</summary>
    public event Action<AttentionAlert>? GoRequested;

    /// <summary>Add alerts and (re)start the countdown. <paramref name="anchor"/> picks the screen.</summary>
    public void Push(IEnumerable<AttentionPopupItem> items, Window anchor, int seconds)
    {
        _anchor = anchor;
        foreach (var item in items)
        {
            Items.Insert(0, item);
            while (Items.Count > MaxRows) Items.RemoveAt(Items.Count - 1);
        }
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Start();
        if (!IsVisible) Show();
        PlaceInCorner();
    }

    /// <summary>Bottom-right of the working area of the screen the main window is on.</summary>
    private void PlaceInCorner()
    {
        var screen = (_anchor is not null ? Screens.ScreenFromWindow(_anchor) : null) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var w = (int)Math.Ceiling(Bounds.Width * scale);
        var h = (int)Math.Ceiling(Bounds.Height * scale);
        var margin = (int)(16 * scale);
        Position = new PixelPoint(area.Right - w - margin, area.Bottom - h - margin);
    }

    private void OnItemClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is AttentionPopupItem item) GoRequested?.Invoke(item.Alert);
        Close();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
