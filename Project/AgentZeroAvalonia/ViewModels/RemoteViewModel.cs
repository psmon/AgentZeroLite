using System.Collections.ObjectModel;
using Agent.Common;
using Agent.Common.Remote;
using AgentZeroAvalonia.Services.Remote;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>One paired browser, as far as the app knows it: the hash of its token.</summary>
public sealed record PairedBrowserRow(string Hash)
{
    public string Label => "🔑 " + (Hash.Length > 12 ? Hash[..12] + "…" : Hash);
}

/// <summary>
/// The Remote page — the WPF host's Remote panel on this host: control this PC's terminals from a
/// browser on a phone or another computer. It edits <c>remote-settings.json</c> (shared with WPF),
/// starts and stops <see cref="RemoteServer"/>, issues the one-time pairing code and revokes paired
/// browsers. Only the download builds have it: the Store MSIX and the Mac App Store build compile
/// it out (<c>-p:IncludeRemote=false</c>).
/// </summary>
public partial class RemoteViewModel : ObservableObject
{
    private readonly RemoteServer _server;
    private readonly RemoteSettings _settings;
    private readonly DispatcherTimer _timer;

    public IReadOnlyList<string> BindChoices { get; } = ["LAN — phones and other PCs on this network", "This PC only (127.0.0.1)"];

    [ObservableProperty] private bool _startAtLaunch;
    [ObservableProperty] private int _bindIndex;
    [ObservableProperty] private decimal _port;
    [ObservableProperty] private decimal _maxConnections;

    [ObservableProperty] private string _statusText = "Stopped";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _pin = "";
    [ObservableProperty] private string _pinExpiry = "";
    [ObservableProperty] private string _connections = "";
    [ObservableProperty] private string _savedNote = "";

    public ObservableCollection<string> Urls { get; } = [];
    public ObservableCollection<PairedBrowserRow> Paired { get; } = [];

    public bool HasError => Error.Length > 0;
    public bool HasPin => Pin.Length > 0;
    public bool HasNoPaired => Paired.Count == 0;
    public bool IsStopped => !IsRunning;

    /// <summary>Copy a URL to the clipboard (set by the view).</summary>
    public Func<string, Task>? CopyText { get; set; }

    /// <summary>Open a URL in this PC's browser (set by the view).</summary>
    public Action<string>? OpenUrl { get; set; }

    internal RemoteViewModel(RemoteServer server, RemoteSettings settings)
    {
        _server = server;
        _settings = settings;
        _server.StatusChanged += () => Dispatcher.UIThread.Post(Refresh);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        LoadSettings();
        Refresh();
    }

    internal RemoteServer Server => _server;

    /// <summary>Start the server if the person left it on ("Start when AgentZero starts").</summary>
    public void AutoStart()
    {
        if (_settings.Enabled) _server.Start(_settings);
    }

    /// <summary>The page polls the PIN countdown and the connection count only while shown.</summary>
    public void SetVisible(bool visible)
    {
        if (visible) { Refresh(); _timer.Start(); }
        else _timer.Stop();
    }

    public void Shutdown()
    {
        _timer.Stop();
        _server.Stop();
    }

    private void LoadSettings()
    {
        StartAtLaunch = _settings.Enabled;
        BindIndex = _settings.BindAddress == "127.0.0.1" ? 1 : 0;
        Port = _settings.Port;
        MaxConnections = _settings.MaxConnections;
    }

    [RelayCommand]
    private void Save()
    {
        _settings.Enabled = StartAtLaunch;
        _settings.BindAddress = BindIndex == 1 ? "127.0.0.1" : "0.0.0.0";
        _settings.Port = (int)Math.Clamp(Port, 1, 65535);
        _settings.MaxConnections = (int)Math.Clamp(MaxConnections, 1, 20);
        try
        {
            RemoteSettingsStore.Save(_settings);
            SavedNote = _server.IsRunning ? "Saved — the server restarted on the new settings." : "Saved.";
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[Remote] settings save failed", ex);
            SavedNote = "Save failed: " + ex.Message;
        }
        _server.UpdateMaxConnections(_settings.MaxConnections);
        if (_server.IsRunning) _server.Start(_settings);
        LoadSettings();
        Refresh();
    }

    [RelayCommand]
    private void Start()
    {
        _server.Start(_settings);
        Refresh();
    }

    [RelayCommand]
    private void Stop()
    {
        _server.Stop();
        Refresh();
    }

    /// <summary>A fresh one-time code; starts the server when it is not running (nothing could use the code otherwise).</summary>
    [RelayCommand]
    private void IssuePin()
    {
        if (!_server.IsRunning) _server.Start(_settings);
        if (!_server.IsRunning) { Refresh(); return; }
        _server.Auth.IssuePin();
        Refresh();
    }

    [RelayCommand]
    private void RevokeAll()
    {
        _server.Auth.RevokeAll();
        Refresh();
    }

    [RelayCommand]
    private void Revoke(PairedBrowserRow? row)
    {
        if (row is null) return;
        _server.Auth.RevokeTokenHash(row.Hash);
        Refresh();
    }

    [RelayCommand]
    private async Task Copy(string? url)
    {
        if (string.IsNullOrEmpty(url) || CopyText is null) return;
        try { await CopyText(url); } catch (Exception ex) { AppLogger.Log($"[Remote] copy failed: {ex.Message}"); }
    }

    [RelayCommand]
    private void Open(string? url)
    {
        if (!string.IsNullOrEmpty(url)) OpenUrl?.Invoke(url);
    }

    /// <summary>Re-read the server's state into the page.</summary>
    public void Refresh()
    {
        IsRunning = _server.IsRunning;
        StatusText = IsRunning
            ? (_server.IsLan ? $"Running on port {_server.Port} — reachable on this network" : $"Running on port {_server.Port} — this PC only")
            : "Stopped";
        Error = _server.LastError ?? "";

        var urls = _server.Urls;
        if (!urls.SequenceEqual(Urls))
        {
            Urls.Clear();
            foreach (var u in urls) Urls.Add(u);
        }

        if (_server.Auth.CurrentPin is { } p)
        {
            Pin = p.Pin;
            var left = p.ExpiresAt - DateTimeOffset.UtcNow;
            PinExpiry = left > TimeSpan.Zero ? $"valid for {(int)left.TotalMinutes}:{left.Seconds:D2} · single use" : "expired";
        }
        else
        {
            Pin = "";
            PinExpiry = "";
        }

        var hashes = _server.Auth.PairedHashes;
        if (!hashes.SequenceEqual(Paired.Select(r => r.Hash)))
        {
            Paired.Clear();
            foreach (var h in hashes) Paired.Add(new PairedBrowserRow(h));
            OnPropertyChanged(nameof(HasNoPaired));
        }

        _ = RefreshConnectionsAsync();
    }

    private async Task RefreshConnectionsAsync()
    {
        var status = IsRunning ? await _server.GetStatusAsync() : null;
        Connections = status is not null ? $"{status.Active} / {status.Max}" : $"0 / {_settings.MaxConnections}";
    }

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnPinChanged(string value) => OnPropertyChanged(nameof(HasPin));
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(IsStopped));

    // ── CLI: remote status|start|stop|pin ────────────────────────────────────

    /// <summary><c>-cli remote &lt;verb&gt;</c> → JSON reply (runs on the UI thread).</summary>
    public string CliCommand(string verb)
    {
        switch (verb)
        {
            case "" or "status":
                break;
            case "start":
                _server.Start(_settings);
                break;
            case "stop":
                _server.Stop();
                break;
            case "pin":
                if (!_server.IsRunning) _server.Start(_settings);
                if (_server.IsRunning)
                {
                    var pin = _server.Auth.IssuePin();
                    AppLogger.Log($"[Remote] PIN issued via CLI (expires {pin.ExpiresAt:HH:mm:ss})");
                    Refresh();
                    return StatusJson(pin.Pin);
                }
                break;
            default:
                return Agent.Common.Platform.CliIpcProtocol.ErrorJson($"unknown remote verb '{verb}' (status | start | stop | pin | open)");
        }
        Refresh();
        return StatusJson(null);
    }

    private string StatusJson(string? pin)
    {
        static string E(string s) => Agent.Common.Platform.CliIpcProtocol.Escape(s);
        var urls = string.Join(",", _server.Urls.Select(u => $"\"{E(u)}\""));
        var ok = _server.IsRunning || pin is null && _server.LastError is null;
        return "{" + $"\"ok\":{(ok ? "true" : "false")},\"running\":{(_server.IsRunning ? "true" : "false")}" +
               $",\"port\":{(_server.IsRunning ? _server.Port : _settings.Port)},\"lan\":{(_settings.BindAddress != "127.0.0.1" ? "true" : "false")}" +
               $",\"url\":\"{E(_server.Urls.FirstOrDefault() ?? "")}\",\"urls\":[{urls}]" +
               $",\"paired\":{_server.Auth.PairedHashes.Count}" +
               (pin is null ? "" : $",\"pin\":\"{pin}\"") +
               (_server.LastError is { } err ? $",\"error\":\"{E(err)}\"" : "") + "}";
    }
}
