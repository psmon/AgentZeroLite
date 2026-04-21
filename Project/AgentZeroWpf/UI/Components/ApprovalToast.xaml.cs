using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentZeroWpf.UI.Components;

public partial class ApprovalToast : UserControl
{
    private DispatcherTimer? _countdownTimer;
    private int _remainingSec;
    private DateTime? _muteUntil;

    /// <summary>Fires when user clicks an option. Args: optionIndex (0-based).</summary>
    public event Action<int>? OptionSelected;

    /// <summary>True while muted (5-min snooze active).</summary>
    public bool IsMuted => _muteUntil.HasValue && DateTime.UtcNow < _muteUntil.Value;

    public ApprovalToast()
    {
        InitializeComponent();
    }

    /// <summary>Show approval toast with options. Auto-hides after 10 seconds.</summary>
    public void Show(string command, List<ApprovalParser.ApprovalOption> options)
    {
        if (IsMuted) return;

        // Set command text
        txtCommand.Text = string.IsNullOrEmpty(command) ? "(unknown command)" : command;

        // Build option buttons
        pnlOptions.Children.Clear();
        for (int i = 0; i < options.Count; i++)
        {
            var opt = options[i];
            int idx = i;
            var btn = new Button
            {
                Content = $"{opt.Number}. {opt.Text}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            };
            btn.Click += (_, _) =>
            {
                OptionSelected?.Invoke(idx);
                Hide();
            };
            pnlOptions.Children.Add(btn);
        }

        // Reset countdown
        _remainingSec = 10;
        txtCountdown.Text = $"{_remainingSec}s";

        toastRoot.Opacity = 1;

        // Start countdown timer
        _countdownTimer?.Stop();
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += OnCountdownTick;
        _countdownTimer.Start();
    }

    public void Hide()
    {
        _countdownTimer?.Stop();
        toastRoot.Opacity = 0;
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        _remainingSec--;
        txtCountdown.Text = $"{_remainingSec}s";

        if (_remainingSec <= 0)
        {
            _countdownTimer?.Stop();
            Hide();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    private void OnMuteChecked(object sender, RoutedEventArgs e)
    {
        _muteUntil = DateTime.UtcNow.AddMinutes(5);
        Hide();
    }
}
