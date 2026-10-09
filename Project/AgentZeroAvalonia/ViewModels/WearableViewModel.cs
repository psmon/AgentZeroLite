using System.Collections.ObjectModel;
using Agent.Common;
using Agent.Common.Wearable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>One allow-listed folder the watch's file tools may reach.</summary>
public partial class AllowedRootRow : ObservableObject
{
    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private bool _writable;
}

/// <summary>
/// The Wearable page (Windows only) — the WPF host's Wearable panel on this host. It edits
/// <c>wearable-settings.json</c> (the whole contract with the host process; "apply" means
/// restart) and starts, stops and tests <c>AgentZeroWearable.exe</c> through
/// <see cref="WearableHostLauncher"/> in ZeroCommon. The voice and the ear are not set
/// here: they come from Settings → Voice, and <see cref="Models"/> says what the host
/// will load from there.
/// </summary>
public partial class WearableViewModel : ObservableObject
{
    private const int MaxLogLines = 500;

    private readonly WearableHostLauncher _host;
    private readonly Queue<string> _log = new();
    private WearableSettings _settings;

    public IReadOnlyList<string> Brains => WearableDiagnostics.Brains;
    public IReadOnlyList<string> CliProviders => WearableDiagnostics.CliProviders;
    public ObservableCollection<AllowedRootRow> Roots { get; } = [];

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _deviceName = "";
    [ObservableProperty] private bool _disableBle;
    [ObservableProperty] private decimal _remotingPort;
    [ObservableProperty] private bool _hudEnabled;
    [ObservableProperty] private decimal _hudPort;
    [ObservableProperty] private string _brain = WearableBrainNames.AgentExternal;
    [ObservableProperty] private string _cliProvider = "echo";
    [ObservableProperty] private bool _webToolsEnabled;
    [ObservableProperty] private string _announceOnConnect = "";
    [ObservableProperty] private decimal _talkOnConnectMs;

    [ObservableProperty] private string _status = "stopped";
    [ObservableProperty] private string _models = "";
    [ObservableProperty] private string _exePath = "";
    [ObservableProperty] private string? _lastError;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _logText = "";

    public bool IsCliBrain => Brain == WearableBrainNames.Cli;
    public bool IsStopped => !IsRunning;
    public bool HasError => !string.IsNullOrEmpty(LastError);
    public bool CanTest => !IsTesting;

    partial void OnBrainChanged(string value) => OnPropertyChanged(nameof(IsCliBrain));
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(IsStopped));
    partial void OnLastErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnIsTestingChanged(bool value) => OnPropertyChanged(nameof(CanTest));

    /// <summary>Raised whenever a log line is appended — the view scrolls to the end.</summary>
    public event Action? LogAppended;

    /// <summary>Asks the view for a folder; null when cancelled. Set by the view.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    public WearableViewModel(string configuration)
    {
        _host = new WearableHostLauncher(configuration);
        _host.LineReceived += line => Dispatcher.UIThread.Post(() => Append(line));
        _host.StatusChanged += () => Dispatcher.UIThread.Post(RefreshStatus);
        _settings = WearableSettingsStore.Load();
        LoadIntoPage();
        RefreshStatus();
    }

    /// <summary>"Start the host when AgentZero starts" — called once by the app.</summary>
    public void AutoStart()
    {
        if (_settings.Enabled && WearableHostLauncher.Supported) _host.Start();
    }

    public void Shutdown() => _host.Stop();

    private void LoadIntoPage()
    {
        var s = _settings;
        Enabled = s.Enabled;
        DeviceName = s.DeviceName;
        DisableBle = s.DisableBle;
        RemotingPort = s.RemotingPort;
        HudEnabled = s.HudEnabled;
        HudPort = s.HudPort;
        Brain = Brains.Contains(s.Brain) ? s.Brain : WearableBrainNames.AgentExternal;
        CliProvider = CliProviders.Contains(s.CliProvider) ? s.CliProvider : "echo";
        WebToolsEnabled = s.WebToolsEnabled;
        AnnounceOnConnect = s.AnnounceOnConnect;
        TalkOnConnectMs = s.TalkOnConnectMs;
        Roots.Clear();
        foreach (var r in s.AllowedRoots)
            Roots.Add(new AllowedRootRow { Alias = r.Alias, Path = r.Path, Writable = r.Writable });
    }

    /// <summary>Writes the page over the stored settings; fields the page does not show keep their values.</summary>
    internal WearableSettings ApplyTo(WearableSettings s)
    {
        s.Enabled = Enabled;
        if (!string.IsNullOrWhiteSpace(DeviceName)) s.DeviceName = DeviceName.Trim();
        s.DisableBle = DisableBle;
        if (RemotingPort is >= 1 and <= 65535) s.RemotingPort = (int)RemotingPort;
        s.HudEnabled = HudEnabled;
        if (HudPort is >= 1 and <= 65535) s.HudPort = (int)HudPort;
        s.Brain = Brain;
        s.CliProvider = CliProvider;
        s.WebToolsEnabled = WebToolsEnabled;
        s.AnnounceOnConnect = AnnounceOnConnect ?? "";
        if (TalkOnConnectMs >= 0) s.TalkOnConnectMs = (int)TalkOnConnectMs;
        s.AllowedRoots = Roots
            .Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => new AllowedRoot { Alias = r.Alias.Trim(), Path = r.Path.Trim(), Writable = r.Writable })
            .ToList();
        s.WorkspaceRoot = "";   // the pre-M0032 single root; AllowedRoots replaces it
        s.Normalize();
        return s;
    }

    [RelayCommand]
    private void SaveAndApply()
    {
        try
        {
            _settings = ApplyTo(WearableSettingsStore.Load());
            WearableSettingsStore.Save(_settings);
            Append("[panel/info] settings saved");
            if (_host.IsRunning)
            {
                Append("[panel/info] settings changed - restarting the host");
                _host.Start();
            }
            LoadIntoPage();
        }
        catch (Exception ex)
        {
            Append($"[panel/error] save failed: {ex.Message}");
        }
        RefreshStatus();
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (PickFolder is null) return;
        var folder = await PickFolder();
        if (string.IsNullOrWhiteSpace(folder)) return;
        Roots.Add(new AllowedRootRow
        {
            Alias = WearableDiagnostics.UniqueAlias(folder, Roots.Select(r => r.Alias)),
            Path = folder,
        });
    }

    [RelayCommand] private void RemoveFolder(AllowedRootRow row) => Roots.Remove(row);

    [RelayCommand] private void Start() => _host.Start();
    [RelayCommand] private void Stop() => _host.Stop();

    [RelayCommand]
    private void ClearLog()
    {
        _log.Clear();
        LogText = "";
    }

    /// <summary>Runs the host once with <c>--ask</c>: the brain answers without the radio or the mutex.</summary>
    [RelayCommand]
    private Task TestBrainAsync() => TestAsync("asking the brain...", async () =>
    {
        var output = await _host.RunOnceAsync(["--ask", "What time is it?", "--session", "panel-test"], TimeSpan.FromSeconds(180));
        foreach (var line in output.Split('\n'))
            if (!string.IsNullOrWhiteSpace(line)) Append(line.TrimEnd());
    });

    /// <summary>
    /// Separates the three usual causes of "the HUD does nothing": host not running, another
    /// process on the port (claude_hud_amoled's ble_bridge.py), or no watch in range.
    /// </summary>
    [RelayCommand]
    private Task TestHudAsync() => TestAsync($"POST http://127.0.0.1:{_settings.HudPort}/status + /event", async () =>
    {
        if (!_host.IsRunning) Append("[panel/warn] the host is not running - the post below should fail");
        try
        {
            var (status, hookEvent) = await WearableDiagnostics.PostHudTestAsync(_settings.HudPort);
            Append("[panel/info] /status -> " + status);
            Append("[panel/info] /event  -> " + hookEvent);
            Append("[panel/info] see the [hud/...] lines: \"-> watch\" means delivered, \"dropped\" means the endpoint works but nothing is connected");
        }
        catch (Exception ex)
        {
            Append($"[panel/error] {ex.Message} - is the host running, or is another process holding :{_settings.HudPort}?");
        }
    });

    private async Task TestAsync(string intro, Func<Task> work)
    {
        if (IsTesting) return;
        IsTesting = true;
        Append("[panel/info] " + intro);
        try { await work(); }
        catch (Exception ex) { Append($"[panel/error] {ex.Message}"); }
        finally { IsTesting = false; }
    }

    private void RefreshStatus()
    {
        IsRunning = _host.IsRunning;
        Status = _host.IsRunning
            ? _host.IsReady ? $"running · ready (pid {_host.ProcessId})" : $"starting… (pid {_host.ProcessId})"
            : "stopped";
        ExePath = _host.ResolveHostPath() ?? "— (ZeroWearable not built)";
        Models = WearableDiagnostics.DescribeModels();
        LastError = _host.LastError;
    }

    private void Append(string line)
    {
        _log.Enqueue(line);
        while (_log.Count > MaxLogLines) _log.Dequeue();
        LogText = string.Join(Environment.NewLine, _log);
        LogAppended?.Invoke();
        AppLogger.Log("[Wearable] " + line);
    }
}
