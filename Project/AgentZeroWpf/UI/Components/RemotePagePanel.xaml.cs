using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using Agent.Common.Remote;
using AgentZeroWpf.Services.Remote;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// Remote feature control panel. Owns nothing itself — MainWindow supplies the shared
/// <see cref="RemoteServerHost"/> (which holds the auth core) and the persisted
/// <see cref="RemoteSettings"/>, so there is a single source of truth. This panel just
/// binds the settings to inputs, drives start/stop/PIN/revoke, and polls live status while
/// visible.
/// </summary>
public partial class RemotePagePanel : UserControl
{
    private RemoteServerHost? _host;
    private RemoteSettings? _settings;
    private readonly DispatcherTimer _timer;

    /// <summary>Raised when the user clicks the panel's close button.</summary>
    public event Action? CloseRequested;

    public RemotePagePanel()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshStatus();
        IsVisibleChanged += OnVisibleChanged;
    }

    /// <summary>Wire the panel to the shared host + settings. Call once from MainWindow.</summary>
    public void Initialize(RemoteServerHost host, RemoteSettings settings)
    {
        _host = host;
        _settings = settings;
        _host.StatusChanged += () => Dispatcher.BeginInvoke(RefreshStatus);
        LoadSettingsIntoUi();
        RefreshStatus();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) { RefreshStatus(); _timer.Start(); }
        else _timer.Stop();
    }

    private void LoadSettingsIntoUi()
    {
        if (_settings is null) return;
        chkEnabled.IsChecked = _settings.Enabled;
        cboBind.SelectedIndex = _settings.BindAddress == "127.0.0.1" ? 1 : 0;
        txtPort.Text = _settings.Port.ToString();
        txtMaxConn.Text = _settings.MaxConnections.ToString();
    }

    private void OnSaveApplyClick(object sender, RoutedEventArgs e)
    {
        if (_settings is null || _host is null) return;

        _settings.Enabled = chkEnabled.IsChecked == true;
        _settings.BindAddress = cboBind.SelectedIndex == 1 ? "127.0.0.1" : "0.0.0.0";
        if (int.TryParse(txtPort.Text, out var port) && port is > 0 and < 65536)
            _settings.Port = port;
        if (int.TryParse(txtMaxConn.Text, out var max) && max > 0)
            _settings.MaxConnections = max;

        RemoteSettingsStore.Save(_settings);

        // Apply live: push the cap, and if the server is up, restart on the new binding.
        _host.UpdateMaxConnections(_settings.MaxConnections);
        if (_host.IsRunning)
            _host.Start(_settings);

        LoadSettingsIntoUi();
        RefreshStatus();
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_settings is null || _host is null) return;
        _host.Start(_settings);
        RefreshStatus();
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _host?.Stop();
        RefreshStatus();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshStatus();

    private void OnIssuePinClick(object sender, RoutedEventArgs e)
    {
        if (_host is null) return;
        var pin = _host.Auth.IssuePin();
        txtPin.Text = pin.Pin;
        RefreshStatus();
    }

    private void OnRevokeAllClick(object sender, RoutedEventArgs e)
    {
        _host?.Auth.RevokeAll();
        RefreshTokens();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void RefreshStatus()
    {
        if (_host is null) return;

        txtStatus.Text = _host.IsRunning ? "실행 중" : "중지됨";
        txtStatus.Foreground = _host.IsRunning
            ? (System.Windows.Media.Brush)FindResource("CyberMintBrush")
            : (System.Windows.Media.Brush)FindResource("TextDim");
        txtUrl.Text = _host.BoundUrl ?? "—";

        if (!string.IsNullOrEmpty(_host.LastError))
        {
            txtError.Text = _host.LastError;
            txtError.Visibility = Visibility.Visible;
        }
        else
        {
            txtError.Visibility = Visibility.Collapsed;
        }

        // PIN countdown.
        var pin = _host.Auth.CurrentPin;
        if (pin is { } p)
        {
            txtPin.Text = p.Pin;
            var remain = p.ExpiresAt - DateTimeOffset.UtcNow;
            txtPinExpiry.Text = remain > TimeSpan.Zero
                ? $"{(int)remain.TotalMinutes}:{remain.Seconds:D2} 남음"
                : "만료됨";
        }
        else
        {
            txtPinExpiry.Text = "";
        }

        // Live connection count.
        _ = UpdateConnectionCountAsync();
        RefreshTokens();
    }

    private async System.Threading.Tasks.Task UpdateConnectionCountAsync()
    {
        if (_host is null) return;
        var status = await _host.GetStatusAsync();
        if (status is not null)
            txtConnCount.Text = $"{status.Active} / {status.Max}";
        else if (_settings is not null)
            txtConnCount.Text = $"0 / {_settings.MaxConnections}";
    }

    private void RefreshTokens()
    {
        if (_host is null) return;
        var hashes = _host.Auth.PairedHashes;
        pnlTokens.Children.Clear();
        txtNoTokens.Visibility = hashes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var hash in hashes)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var revoke = new Button
            {
                Content = "해제",
                Style = (Style)FindResource("FlatButton"),
                FontSize = 11,
                Tag = hash,
            };
            revoke.Click += OnRevokeOneClick;
            DockPanel.SetDock(revoke, Dock.Right);
            row.Children.Add(revoke);

            row.Children.Add(new TextBlock
            {
                Text = "🔑 " + (hash.Length > 12 ? hash.Substring(0, 12) + "…" : hash),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            pnlTokens.Children.Add(row);
        }
    }

    private void OnRevokeOneClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hash })
        {
            _host?.Auth.RevokeTokenHash(hash);
            RefreshTokens();
        }
    }
}
