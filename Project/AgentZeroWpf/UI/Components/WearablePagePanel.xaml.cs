using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

using Agent.Common.Llm;
using Agent.Common.Voice;
using Agent.Common.Wearable;
using AgentZeroWpf.Services.Wearable;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// Wearable host control panel. Mirrors <see cref="RemotePagePanel"/>: MainWindow owns the
/// <see cref="WearableHostProcess"/> and the persisted <see cref="WearableSettings"/>, and
/// this panel binds them to inputs, drives start/stop, and shows the host's output.
///
/// <para>Deliberately not here: voice, STT and LLM pickers. The host reads
/// <c>voice-settings.json</c> and <c>llm-settings.json</c> itself, so duplicating those
/// controls would create a second source of truth for the same model. What the panel does
/// instead is <i>report</i> what those settings currently resolve to
/// (<see cref="DescribeModels"/>), so a silent watch can be diagnosed without leaving the
/// screen.</para>
/// </summary>
public partial class WearablePagePanel : UserControl
{
    /// <summary>Bounded so a host left running overnight cannot grow the panel without limit.</summary>
    private const int MaxLogLines = 500;

    private WearableHostProcess? _host;
    private WearableSettings? _settings;
    private readonly Queue<string> _log = new();

    /// <summary>One editable row of the allow-list (M0032). Plain properties: the TextBoxes
    /// push into it on every keystroke and Save reads it back; nothing observes it.</summary>
    public sealed class RootRow
    {
        public string Alias { get; set; } = "";
        public string Path { get; set; } = "";
        public bool Writable { get; set; }
    }

    private readonly ObservableCollection<RootRow> _roots = new();

    /// <summary>Raised when the user clicks the panel's close button.</summary>
    public event Action? CloseRequested;

    public WearablePagePanel()
    {
        InitializeComponent();
    }

    /// <summary>Wire the panel to the shared host + settings. Call once from MainWindow.</summary>
    public void Initialize(WearableHostProcess host, WearableSettings settings)
    {
        _host = host;
        _settings = settings;
        _host.StatusChanged += () => Dispatcher.BeginInvoke(RefreshStatus);
        _host.LineReceived += line => Dispatcher.BeginInvoke(() => AppendLog(line));
        LoadSettingsIntoUi();
        RefreshStatus();
    }

    // ── Settings ↔ UI ────────────────────────────────────────────────────────

    private void LoadSettingsIntoUi()
    {
        if (_settings is null) return;
        chkEnabled.IsChecked = _settings.Enabled;
        txtDevice.Text = _settings.DeviceName;
        chkNoBle.IsChecked = _settings.DisableBle;
        txtPort.Text = _settings.RemotingPort.ToString();
        chkHud.IsChecked = _settings.HudEnabled;
        txtHudPort.Text = _settings.HudPort.ToString();
        cboBrain.SelectedIndex = _settings.Brain switch
        {
            WearableBrainNames.AgentLocal => 1,
            WearableBrainNames.Cli => 2,
            _ => 0,
        };
        cboCli.SelectedIndex = _settings.CliProvider?.ToLowerInvariant() switch
        {
            "claude" => 1,
            "netclaw" => 2,
            _ => 0,
        };
        _roots.Clear();
        foreach (var root in _settings.Normalize().AllowedRoots)
            _roots.Add(new RootRow { Alias = root.Alias, Path = root.Path, Writable = root.Writable });
        lstRoots.ItemsSource = _roots;
        chkWebTools.IsChecked = _settings.WebToolsEnabled;
        txtAnnounce.Text = _settings.AnnounceOnConnect;
        txtTalkMs.Text = _settings.TalkOnConnectMs.ToString();
        UpdateBrainEnablement();
    }

    private void OnSaveApplyClick(object sender, RoutedEventArgs e)
    {
        if (_settings is null || _host is null) return;

        _settings.Enabled = chkEnabled.IsChecked == true;
        if (!string.IsNullOrWhiteSpace(txtDevice.Text)) _settings.DeviceName = txtDevice.Text.Trim();
        _settings.DisableBle = chkNoBle.IsChecked == true;
        if (int.TryParse(txtPort.Text, out var port) && port is > 0 and < 65536)
            _settings.RemotingPort = port;
        _settings.HudEnabled = chkHud.IsChecked == true;
        if (int.TryParse(txtHudPort.Text, out var hudPort) && hudPort is > 0 and < 65536)
            _settings.HudPort = hudPort;
        _settings.Brain = cboBrain.SelectedIndex switch
        {
            1 => WearableBrainNames.AgentLocal,
            2 => WearableBrainNames.Cli,
            _ => WearableBrainNames.AgentExternal,
        };
        _settings.CliProvider = cboCli.SelectedIndex switch
        {
            1 => "claude",
            2 => "netclaw",
            _ => "echo",
        };
        _settings.AllowedRoots = _roots
            .Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => new AllowedRoot { Alias = r.Alias, Path = r.Path.Trim(), Writable = r.Writable })
            .ToList();
        _settings.WorkspaceRoot = "";   // legacy field; the list above is the contract now
        _settings.WebToolsEnabled = chkWebTools.IsChecked == true;
        _settings.Normalize();
        _settings.AnnounceOnConnect = txtAnnounce.Text.Trim();
        if (int.TryParse(txtTalkMs.Text, out var talkMs) && talkMs >= 0)
            _settings.TalkOnConnectMs = talkMs;

        WearableSettingsStore.Save(_settings);

        // The host reads its settings once, at startup — a BLE handle cannot be moved to a
        // new device name in place — so applying means restarting whatever is running.
        if (_host.IsRunning)
        {
            AppendLog("[panel/info] settings changed - restarting the host");
            _host.Start();
        }

        LoadSettingsIntoUi();
        RefreshStatus();
    }

    private void OnBrainChanged(object sender, SelectionChangedEventArgs e) => UpdateBrainEnablement();

    /// <summary>The CLI picker only means something for the CLI brain; grey it out otherwise
    /// rather than letting a stale value look like it is in effect.</summary>
    private void UpdateBrainEnablement()
    {
        if (cboCli is null) return;
        cboCli.IsEnabled = cboBrain.SelectedIndex == 2;
    }

    private void OnAddRootClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "A folder the watch's file tools may reach (everything outside the list is refused)",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var path = dialog.SelectedPath;
        var alias = AllowedRoot.NormalizeAlias(System.IO.Path.GetFileName(path.TrimEnd('\\', '/')));
        if (alias.Length == 0) alias = "root";
        var unique = alias;
        for (var n = 2; _roots.Any(r => string.Equals(r.Alias, unique, StringComparison.OrdinalIgnoreCase)); n++)
            unique = $"{alias}-{n}";
        _roots.Add(new RootRow { Alias = unique, Path = path, Writable = false });
    }

    private void OnRemoveRootClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RootRow row) _roots.Remove(row);
    }

    // ── Host control ─────────────────────────────────────────────────────────

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        _host?.Start();
        RefreshStatus();
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _host?.Stop();
        RefreshStatus();
    }

    /// <summary>
    /// Runs the host once with <c>--ask</c>. That path touches neither the radio nor the
    /// single-instance mutex, so it is safe while the real host is up — which is exactly
    /// when you want to know whether the brain answers.
    /// </summary>
    private async void OnTestBrainClick(object sender, RoutedEventArgs e)
    {
        var exe = WearableHostProcess.ResolveHostPath();
        if (exe is null)
        {
            AppendLog("[panel/error] AgentZeroWearable.exe not found - build Project/ZeroWearable");
            return;
        }

        btnTestBrain.IsEnabled = false;
        AppendLog("[panel/info] asking the brain...");
        try
        {
            var output = await Task.Run(() => RunOnce(exe, ["--ask", "What time is it?", "--session", "panel-test"]));
            foreach (var line in output.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) AppendLog(line.TrimEnd());
        }
        catch (Exception ex)
        {
            AppendLog($"[panel/error] {ex.Message}");
        }
        finally
        {
            btnTestBrain.IsEnabled = true;
        }
    }

    /// <summary>
    /// Posts one status + one event to the HUD endpoint, shaped like the scripts in
    /// <c>~/.claude/hud_amoled</c> do. "The HUD does nothing" is nearly always one of three
    /// things — the host is not running, another process (claude_hud_amoled's
    /// <c>ble_bridge.py</c>) already holds :8765, or the watch is out of range — and this
    /// separates them: the reply here says whether the endpoint accepted the line, and the
    /// <c>[hud/…]</c> line that follows says whether it reached the watch.
    /// </summary>
    private async void OnTestHudClick(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        if (_host is { IsRunning: false })
            AppendLog("[panel/warn] the host is not running - the post below should fail");

        btnTestHud.IsEnabled = false;
        var port = _settings.HudPort;
        try
        {
            const string status =
                """{"session":"panel-test","model":"AgentZero panel","cost_usd":0,"context_used_pct":0,"label":"wearable"}""";
            const string hookEvent =
                """{"type":"tool","tool":"Panel","target":"Test HUD","msg":"HUD test from the Wearable panel","session":"panel-test"}""";

            AppendLog($"[panel/info] POST http://127.0.0.1:{port}/status + /event");
            AppendLog("[panel/info] /status -> " + await PostAsync(port, "/status", status));
            AppendLog("[panel/info] /event  -> " + await PostAsync(port, "/event", hookEvent));
            AppendLog("[panel/info] see the [hud/...] lines: \"-> watch\" means delivered, " +
                      "\"dropped\" means the endpoint works but nothing is connected");
        }
        catch (Exception ex)
        {
            AppendLog($"[panel/error] {ex.Message} - is the host running, or is another " +
                      $"process holding :{port}?");
        }
        finally
        {
            btnTestHud.IsEnabled = true;
        }
    }

    private static async Task<string> PostAsync(int port, string path, string json)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        var reply = await http.PostAsync($"http://127.0.0.1:{port}{path}", body);
        return $"{(int)reply.StatusCode} {await reply.Content.ReadAsStringAsync()}";
    }

    private static string RunOnce(string exe, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(WearableSettingsStore.DefaultFilePath);
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {exe}");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        // An LLM can be slow; a hung child must not hold the UI thread's Task forever.
        if (!process.WaitForExit(180_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return stdout + stderr + "\n[panel/error] the test did not finish within 180 s";
        }
        return stdout + stderr;
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        txtLog.Text = "";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    // ── Status ───────────────────────────────────────────────────────────────

    private void RefreshStatus()
    {
        if (_host is null) return;

        txtStatus.Text = _host.IsRunning
            ? _host.IsReady
                ? $"running · ready (pid {_host.ProcessId})"
                : $"starting… (pid {_host.ProcessId})"
            : "stopped";

        txtExePath.Text = WearableHostProcess.ResolveHostPath() ?? "— (ZeroWearable not built)";
        txtModels.Text = DescribeModels();

        var error = _host.LastError;
        txtError.Text = error ?? "";
        txtError.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;

        btnStart.IsEnabled = !_host.IsRunning;
        btnStop.IsEnabled = _host.IsRunning;
    }

    /// <summary>
    /// One line saying what the host will actually load, resolved from the same stores it
    /// reads. This is the answer to "why does my watch not speak?" — usually because TTS is
    /// not Supertonic, or the bundle was never downloaded.
    /// </summary>
    private static string DescribeModels()
    {
        try
        {
            var voice = VoiceSettingsStore.Load();
            var llm = LlmSettingsStore.Load();

            var tts = string.Equals(voice.TtsProvider, TtsProviderNames.Supertonic, StringComparison.OrdinalIgnoreCase)
                ? SuperTonicModelStore.IsModelPresent(SuperTonicModelStore.ResolveModelDir(voice))
                    ? $"voice Supertonic {voice.SupertonicVoice}/{voice.SupertonicLanguage}"
                    : "voice Supertonic (model not installed → text only)"
                : $"voice off (TTS = {voice.TtsProvider})";

            var stt = string.Equals(voice.SttProvider, SttProviderNames.WhisperLocal, StringComparison.OrdinalIgnoreCase)
                ? WhisperModelStore.IsDownloaded(voice.SttWhisperModel)
                    ? $"ear whisper-{voice.SttWhisperModel}"
                    : $"ear whisper-{voice.SttWhisperModel} (model not installed → mic off)"
                : $"ear off (STT = {voice.SttProvider})";

            var brain = llm.ActiveBackend == LlmActiveBackend.Local
                ? $"brain on-device {LlmModelCatalog.FindById(llm.ModelId).Id}"
                : $"brain {llm.External.Provider} / {llm.ResolveExternalModel()}";

            return $"{tts} · {stt} · {brain}";
        }
        catch (Exception ex)
        {
            return $"could not read the settings: {ex.Message}";
        }
    }

    private void AppendLog(string line)
    {
        _log.Enqueue(line);
        while (_log.Count > MaxLogLines) _log.Dequeue();
        txtLog.Text = string.Join(Environment.NewLine, _log);
        logScroll.ScrollToEnd();
    }
}
