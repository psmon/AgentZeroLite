using System.Collections.ObjectModel;
using Agent.Common;
using Agent.Common.Agents;
using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Agent.Common.Llm;
using Agent.Common.Llm.Providers;
using Agent.Common.Module;
using Agent.Common.Security;
using Agent.Common.Services;
using AgentZeroAvalonia.Security;
using AgentZeroAvalonia.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>Declared in the order the page lists them (the palette walks this enum): the terminal
/// settings first, because the terminal is what the app is for.</summary>
public enum SettingsSection { Terminal, Cli, Shortcuts, Llm, Mcp, Voice, Models, Notifications, Budget }

/// <summary>A catalog model for the local-LLM combo.</summary>
public sealed record ModelChoice(LlmModelCatalogEntry Entry)
{
    public string DisplayName => Entry.DisplayName;
}

/// <summary>
/// One CLI definition being edited (M0038). Mirrors the WPF definition dialog's rules:
/// built-ins cannot be deleted, a typed password is protected once and the stored
/// ciphertext is kept when nothing new was typed, SSH auth is PublicKey or Password.
/// </summary>
public partial class CliDefinitionItem : ObservableObject
{
    public int Id { get; }
    public bool IsBuiltIn { get; }
    public int SortOrder { get; set; }
    private readonly string? _storedEncryptedPassword;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _exePath = "";
    [ObservableProperty] private string? _arguments;
    [ObservableProperty] private bool _reducedMotion;
    [ObservableProperty] private bool _isRemote;
    [ObservableProperty] private string? _sshHost;
    [ObservableProperty] private string? _sshUser;
    [ObservableProperty] private bool _usePublicKey = true;
    [ObservableProperty] private string? _sshKeyPath;
    [ObservableProperty] private string? _newPassword;

    // ── Shell + command: the shell-specific "run this, then stay open" flags are written by
    //    ShellLaunch, so a person types `claude`, not `-NoExit -Command claude` ──
    /// <summary>What to start in the shell (<c>claude</c>); empty for the bare shell.</summary>
    [ObservableProperty] private string _runCommand = "";
    /// <summary>Edit the stored arguments by hand — on by itself when they are not what ShellLaunch writes.</summary>
    [ObservableProperty] private bool _rawArguments;

    public ShellFamily Family => ShellLaunch.FamilyOf(ExePath);
    public bool IsShell => Family != ShellFamily.Other;
    /// <summary>The command box drives the arguments (a known shell, not in raw mode).</summary>
    public bool UsesRunCommand => IsShell && !RawArguments;
    public bool ShowArguments => !UsesRunCommand;
    /// <summary>What the tab will actually run, so the composed flags are visible, not hidden.</summary>
    public string LaunchPreview => string.IsNullOrWhiteSpace(Arguments) ? ExePath.Trim() : $"{ExePath.Trim()} {Arguments!.Trim()}";

    partial void OnExePathChanged(string value)
    {
        Recompose();
        OnPropertyChanged(nameof(Family));
        OnPropertyChanged(nameof(IsShell));
        OnPropertyChanged(nameof(UsesRunCommand));
        OnPropertyChanged(nameof(ShowArguments));
        OnPropertyChanged(nameof(LaunchPreview));
    }

    partial void OnRunCommandChanged(string value) => Recompose();
    partial void OnArgumentsChanged(string? value) => OnPropertyChanged(nameof(LaunchPreview));

    partial void OnRawArgumentsChanged(bool value)
    {
        // Back from raw: keep a hand edit that still reads as a command, else rebuild from the box.
        if (!value && ShellLaunch.TryExtract(ExePath, Arguments, out var command)) RunCommand = command;
        Recompose();
        OnPropertyChanged(nameof(UsesRunCommand));
        OnPropertyChanged(nameof(ShowArguments));
    }

    private void Recompose()
    {
        if (UsesRunCommand) Arguments = ShellLaunch.Compose(ExePath, RunCommand);
    }

    public CliDefinitionItem(CliDefinition e)
    {
        Id = e.Id;
        IsBuiltIn = e.IsBuiltIn;
        SortOrder = e.SortOrder;
        _name = e.Name;
        _exePath = e.ExePath;
        _arguments = e.Arguments;
        _reducedMotion = e.ReducedMotion;
        _isRemote = e.IsRemote;
        _sshHost = e.SshHost;
        _sshUser = e.SshUser;
        _usePublicKey = !string.Equals(e.SshAuthMethod, SshCommandBuilder.AuthMethodPassword, StringComparison.OrdinalIgnoreCase);
        _sshKeyPath = e.SshKeyPath;
        _storedEncryptedPassword = e.EncryptedPassword;
        if (ShellLaunch.TryExtract(e.ExePath, e.Arguments, out var command)) _runCommand = command;
        else _rawArguments = ShellLaunch.FamilyOf(e.ExePath) != ShellFamily.Other;
    }

    public string PasswordHint => string.IsNullOrEmpty(_storedEncryptedPassword) ? "(none)" : "(unchanged)";

    /// <summary>Null when the definition can be saved; otherwise what is missing.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "A name is required.";
        if (string.IsNullOrWhiteSpace(ExePath)) return "An executable is required.";
        // Both halves, because ssh is launched as user@host: with one missing the
        // composer has no command to run and the tab would start a bare local shell.
        if (IsRemote && string.IsNullOrWhiteSpace(SshHost)) return "SSH needs a host.";
        if (IsRemote && string.IsNullOrWhiteSpace(SshUser)) return "SSH needs a user.";
        return null;
    }

    /// <summary>Copy the edited fields onto the entity; a new password is protected, an untouched one is kept.</summary>
    public void ApplyTo(CliDefinition target, Func<string, string> protect)
    {
        target.Name = Name.Trim();
        target.ExePath = ExePath.Trim();
        target.Arguments = string.IsNullOrWhiteSpace(Arguments) ? null : Arguments.Trim();
        target.ReducedMotion = ReducedMotion;
        target.IsRemote = IsRemote;
        target.SshHost = string.IsNullOrWhiteSpace(SshHost) ? null : SshHost.Trim();
        target.SshUser = string.IsNullOrWhiteSpace(SshUser) ? null : SshUser.Trim();
        target.SshAuthMethod = UsePublicKey ? SshCommandBuilder.AuthMethodPublicKey : SshCommandBuilder.AuthMethodPassword;
        target.SshKeyPath = string.IsNullOrWhiteSpace(SshKeyPath) ? null : SshKeyPath.Trim();
        target.EncryptedPassword = string.IsNullOrEmpty(NewPassword) ? _storedEncryptedPassword : protect(NewPassword);
    }
}

/// <summary>
/// The settings page (M0038): LLM (External on every OS, Local on Windows), CLI
/// definitions (CRUD over the shared database), terminal appearance (live). Every store
/// is the WPF host's — <see cref="LlmSettingsStore"/>, <see cref="AppDbContext"/>,
/// <see cref="TerminalSettingsStore"/> — so both hosts read the same files and rows.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private SettingsSection _section = SettingsSection.Terminal;

    public bool IsLlm => Section == SettingsSection.Llm;
    public bool IsCli => Section == SettingsSection.Cli;
    public bool IsTerminal => Section == SettingsSection.Terminal;
    public bool IsVoice => Section == SettingsSection.Voice;
    public bool IsMcp => Section == SettingsSection.Mcp;
    public bool IsModels => Section == SettingsSection.Models;

    /// <summary>Settings → MCP: the servers (and per-tool permissions) the bot's AI mode may use — mcp-settings.json.</summary>
    public McpSettingsViewModel Mcp { get; } = new();

    /// <summary>Settings → Voice: its own view-model, the same voice-settings.json the WPF host and the wearable host read.</summary>
    public VoiceSettingsViewModel Voice { get; } = new();

    /// <summary>Settings → Models: the plugins' on-device models (AST, Florence-2, Sherpa).</summary>
    public OnDeviceModelsViewModel OnDeviceModels { get; } = new();

    /// <summary>Settings → Notifications: the "an agent needs you" popup and flash.</summary>
    public NotificationSettingsViewModel Notifications { get; } = new();

    public bool IsNotifications => Section == SettingsSection.Notifications;

    /// <summary>Settings → Budget: month-to-date agent spend, the cap, price overrides.</summary>
    public BudgetViewModel Budget { get; } = new();

    public bool IsBudget => Section == SettingsSection.Budget;

    /// <summary>Settings → Shortcuts: every chord this host runs, rebindable (shortcut-settings.json, shared with WPF).</summary>
    public ShortcutsViewModel Shortcuts { get; } = new();

    public bool IsShortcuts => Section == SettingsSection.Shortcuts;

    partial void OnSectionChanged(SettingsSection value)
    {
        OnPropertyChanged(nameof(IsNotifications));
        OnPropertyChanged(nameof(IsBudget));
        OnPropertyChanged(nameof(IsShortcuts));
        if (value == SettingsSection.Shortcuts) Shortcuts.Load();
        if (value == SettingsSection.Budget) Budget.Refresh();
        if (value == SettingsSection.Terminal) EnsureInstalledFonts();
        if (value == SettingsSection.Llm) EnsureExternalModels();
        OnPropertyChanged(nameof(IsLlm));
        OnPropertyChanged(nameof(IsCli));
        OnPropertyChanged(nameof(IsTerminal));
        OnPropertyChanged(nameof(IsVoice));
        OnPropertyChanged(nameof(IsModels));
        if (value == SettingsSection.Models) OnDeviceModels.RefreshCommand.Execute(null);
        OnPropertyChanged(nameof(IsMcp));
    }

    [RelayCommand] private void ShowLlm() => Section = SettingsSection.Llm;
    [RelayCommand] private void ShowCli() => Section = SettingsSection.Cli;
    [RelayCommand] private void ShowTerminal() => Section = SettingsSection.Terminal;
    [RelayCommand] private void ShowVoice() => Section = SettingsSection.Voice;
    [RelayCommand] private void ShowMcp() => Section = SettingsSection.Mcp;
    [RelayCommand] private void ShowModels() => Section = SettingsSection.Models;
    [RelayCommand] private void ShowNotifications() => Section = SettingsSection.Notifications;
    [RelayCommand] private void ShowBudget() => Section = SettingsSection.Budget;
    [RelayCommand] private void ShowShortcuts() => Section = SettingsSection.Shortcuts;

    /// <summary>The definitions changed — the shell reloads its new-tab menu.</summary>
    public event Action? CliDefinitionsChanged;

    /// <summary>Terminal appearance saved — open terminals re-post their config.</summary>
    public event Action? AppearanceChanged;

    public SettingsViewModel()
    {
        LocalSupported = OperatingSystem.IsWindows();
        LocalTooltip = LocalSupported ? "LLamaSharp with the bundled llama.cpp (CPU or Vulkan)" : "The local LLM runs on Windows only; use External here.";
        SshEditable = OperatingSystem.IsWindows();
        LoadLlm();
        LoadCli();
        LoadTerminal();
        LlmService.StateChanged += s => Dispatcher.UIThread.Post(() => LocalState = s.ToString());
    }

    // ═══ LLM ════════════════════════════════════════════════════════════════

    public IReadOnlyList<string> Providers { get; } = ExternalProviderNames.All;
    public bool LocalSupported { get; }
    public string LocalTooltip { get; }

    [ObservableProperty] private bool _useExternal = true;
    [ObservableProperty] private bool _useLocal;
    [ObservableProperty] private string _provider = ExternalProviderNames.Ollama;
    [ObservableProperty] private string _externalModel = "";
    [ObservableProperty] private decimal? _externalMaxTokens = 4096;
    [ObservableProperty] private decimal? _temperature = 0.7m;
    [ObservableProperty] private decimal? _agentLoopMaxTurns = LlmRuntimeSettings.DefaultAgentLoopMaxTurns;
    [ObservableProperty] private string _openAIApiKey = "";
    [ObservableProperty] private string _openAIBaseUrl = "";
    [ObservableProperty] private string _lMStudioApiKey = "";
    [ObservableProperty] private string _lMStudioBaseUrl = "";
    [ObservableProperty] private string _ollamaBaseUrl = "";
    [ObservableProperty] private string _llmStatus = "";
    [ObservableProperty] private bool _isTesting;

    public bool ShowOpenAI => Provider == ExternalProviderNames.OpenAI;
    public bool ShowLMStudio => Provider == ExternalProviderNames.LMStudio;
    public bool ShowOllama => Provider == ExternalProviderNames.Ollama;

    partial void OnProviderChanged(string value)
    {
        OnPropertyChanged(nameof(ShowOpenAI));
        OnPropertyChanged(nameof(ShowLMStudio));
        OnPropertyChanged(nameof(ShowOllama));
        // Another server serves other models: the old list would only mislead.
        ExternalModels.Clear();
        if (_modelsRequested) _ = RefreshExternalModelsAsync();
    }

    // ── External model list: what the server says it serves, so the name is picked, not recalled ──
    public ObservableCollection<string> ExternalModels { get; } = new();
    [ObservableProperty] private string _modelListStatus = "";
    [ObservableProperty] private bool _isListingModels;
    private bool _modelsRequested;
    private int _modelListVersion;
    private int _hiddenModels;

    /// <summary>First time the LLM page shows External, ask the server once.</summary>
    private void EnsureExternalModels()
    {
        if (_modelsRequested || !UseExternal) return;
        _modelsRequested = true;
        _ = RefreshExternalModelsAsync();
    }

    partial void OnExternalModelChanged(string value) => NoteModelNotListed();

    private void NoteModelNotListed()
    {
        if (ExternalModels.Count == 0 || IsListingModels) return;
        var name = ExternalModel.Trim();
        var listed = $"{ExternalModels.Count} chat models from {Provider}"
                     + (_hiddenModels > 0 ? $" ({_hiddenModels} embedding / speech / image / snapshot hidden)" : "");
        ModelListStatus = name.Length > 0 && !ExternalModels.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? $"{listed} · \"{name}\" is not one of them — check the name"
            : listed;
    }

    /// <summary>
    /// GET {base}/v1/models with the URL and key on screen (not the saved ones — the point is to
    /// check what was just typed). A newer request wins: switching provider mid-request must not
    /// fill the list with the previous server's models.
    /// </summary>
    [RelayCommand]
    private async Task RefreshExternalModelsAsync()
    {
        _modelsRequested = true;
        var version = ++_modelListVersion;
        IsListingModels = true;
        ModelListStatus = $"Asking {Provider} for its models…";
        try
        {
            var provider = CollectLlm().CreateExternalProvider();
            if (provider is null) { ModelListStatus = "Pick a provider first."; return; }
            List<string> names;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var models = await Task.Run(() => provider.ListModelsAsync(cts.Token));
                var all = models.Select(m => m.Id).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                names = ChatModelFilter.Filter(all).ToList();
                _hiddenModels = all.Count - names.Count;
            }
            finally
            {
                (provider as IDisposable)?.Dispose();
            }
            if (version != _modelListVersion) return;
            ExternalModels.Clear();
            foreach (var n in names) ExternalModels.Add(n);
            IsListingModels = false;
            if (names.Count == 0)
                ModelListStatus = _hiddenModels > 0
                    ? $"{Provider} lists {_hiddenModels} models, none of them a chat model — type the name if one is."
                    : $"{Provider} lists no models — load or pull one there, or type the name.";
            else NoteModelNotListed();
        }
        catch (Exception ex)
        {
            if (version != _modelListVersion) return;
            ModelListStatus = $"Could not list models ({ex.GetType().Name}: {ex.Message}) — check the URL / key, or type the name.";
        }
        finally
        {
            if (version == _modelListVersion) IsListingModels = false;
        }
    }

    partial void OnUseExternalChanged(bool value)
    {
        if (value) UseLocal = false;
        if (value && IsLlm) EnsureExternalModels();
    }
    partial void OnUseLocalChanged(bool value) { if (value) UseExternal = false; }

    // Local (Windows)
    public ObservableCollection<ModelChoice> Models { get; } = new(LlmModelCatalog.All.Select(e => new ModelChoice(e)));
    [ObservableProperty] private ModelChoice? _selectedModel;
    [ObservableProperty] private string _modelStatus = "";
    [ObservableProperty] private bool _localBackendVulkan;
    public ObservableCollection<string> VulkanDevices { get; } = new();
    [ObservableProperty] private int _vulkanDeviceIndex;
    [ObservableProperty] private decimal? _contextSize = 4096;
    [ObservableProperty] private decimal? _gpuLayerCount = 999;
    [ObservableProperty] private string _localState = LlmService.State.ToString();
    [ObservableProperty] private string _localStatus = "";

    /// <summary>Load the local model when AgentZero starts (off by default — the first AI-mode turn loads it anyway).</summary>
    [ObservableProperty] private bool _preloadLocalLlm = Agent.Common.Models.ModelPreloadSettingsStore.Load().LocalLlm;

    partial void OnPreloadLocalLlmChanged(bool value)
    {
        var p = Agent.Common.Models.ModelPreloadSettingsStore.Load();
        p.LocalLlm = value;
        Agent.Common.Models.ModelPreloadSettingsStore.Save(p);
    }
    [ObservableProperty] private bool _isDownloading;

    partial void OnSelectedModelChanged(ModelChoice? value) => RefreshModelStatus();

    private void RefreshModelStatus()
    {
        if (SelectedModel is null) { ModelStatus = ""; return; }
        var path = LlmModelLocator.ResolveExistingOrTarget(SelectedModel.Entry);
        ModelStatus = File.Exists(path)
            ? $"{path}  ({new FileInfo(path).Length / (1024.0 * 1024 * 1024):0.0} GB)"
            : $"not downloaded — will be saved to {path}";
    }

    private void LoadLlm()
    {
        LlmRuntimeSettings s;
        try { s = LlmSettingsStore.Load(); } catch (Exception ex) { LlmStatus = "Could not read the LLM settings: " + ex.Message; s = new LlmRuntimeSettings(); }
        UseExternal = s.ActiveBackend == LlmActiveBackend.External || !LocalSupported;
        UseLocal = !UseExternal;
        // A settings file written before the bundled hosts were removed may name one
        // that no longer ships; fall back rather than showing an empty selection.
        Provider = Providers.Contains(s.External.Provider) ? s.External.Provider : ExternalProviderNames.Ollama;
        ExternalModel = s.External.SelectedModel;
        ExternalMaxTokens = s.External.MaxTokens;
        Temperature = (decimal)s.Temperature;
        AgentLoopMaxTurns = s.ResolveAgentLoopMaxTurns();
        OpenAIApiKey = s.External.OpenAIApiKey;
        OpenAIBaseUrl = s.External.OpenAIBaseUrl;
        LMStudioApiKey = s.External.LMStudioApiKey;
        LMStudioBaseUrl = s.External.LMStudioBaseUrl;
        OllamaBaseUrl = s.External.OllamaBaseUrl;

        SelectedModel = Models.FirstOrDefault(m => m.Entry.Id == s.ModelId) ?? Models.FirstOrDefault();
        LocalBackendVulkan = s.Backend == LocalLlmBackend.Vulkan;
        ContextSize = s.ContextSize;
        GpuLayerCount = s.GpuLayerCount;
        if (LocalSupported)
        {
            try
            {
                VulkanDevices.Clear();
                foreach (var d in VulkanDeviceEnumerator.Enumerate()) VulkanDevices.Add($"{d.Index}: {d.Name} ({d.VendorName}{(d.IsDiscrete ? ", discrete" : "")})");
            }
            catch { }
            VulkanDeviceIndex = Math.Clamp(s.VulkanDeviceIndex, 0, Math.Max(0, VulkanDevices.Count - 1));
        }
    }

    /// <summary>Write the edited LLM fields back into the stored settings (other fields untouched).</summary>
    private LlmRuntimeSettings CollectLlm()
    {
        var s = LlmSettingsStore.Load();
        s.ActiveBackend = UseLocal && LocalSupported ? LlmActiveBackend.Local : LlmActiveBackend.External;
        s.External.Provider = Provider;
        s.External.SelectedModel = ExternalModel.Trim();
        s.External.MaxTokens = (int)Math.Clamp(ExternalMaxTokens ?? 4096, 256, 32768);
        s.Temperature = (float)Math.Clamp(Temperature ?? 0.7m, 0m, 2m);
        // Assign, then let the settings object clamp it: one copy of the bounds, so the
        // spinner and a hand-edited file cannot disagree about what is allowed.
        s.AgentLoopMaxTurns = (int)(AgentLoopMaxTurns ?? LlmRuntimeSettings.DefaultAgentLoopMaxTurns);
        s.AgentLoopMaxTurns = s.ResolveAgentLoopMaxTurns();
        s.External.OpenAIApiKey = OpenAIApiKey.Trim();
        s.External.OpenAIBaseUrl = OpenAIBaseUrl.Trim();
        s.External.LMStudioApiKey = LMStudioApiKey.Trim();
        s.External.LMStudioBaseUrl = LMStudioBaseUrl.Trim();
        s.External.OllamaBaseUrl = OllamaBaseUrl.Trim();
        if (SelectedModel is not null) s.ModelId = SelectedModel.Entry.Id;
        s.Backend = LocalBackendVulkan ? LocalLlmBackend.Vulkan : LocalLlmBackend.Cpu;
        s.ContextSize = (uint)Math.Clamp(ContextSize ?? 4096, 1024, 32768);
        s.GpuLayerCount = (int)Math.Clamp(GpuLayerCount ?? 999, 0, 999);
        s.VulkanDeviceIndex = VulkanDevices.Count == 0 ? -1 : VulkanDeviceIndex;
        return s;
    }

    [RelayCommand]
    private void SaveLlm()
    {
        try
        {
            var s = CollectLlm();
            LlmSettingsStore.Save(s);
            LlmStatus = $"Saved · backend={s.ActiveBackend} · {(s.ActiveBackend == LlmActiveBackend.External ? s.External.Provider + " / " + s.ResolveExternalModel() : s.ModelId)} · tool chain up to {s.ResolveAgentLoopMaxTurns()} turns · keys are stored protected ({SecretProtection.Protector.GetType().Name}).";
            AppLogger.Log($"[Settings] LLM saved | {LlmStatus}");
        }
        catch (Exception ex)
        {
            LlmStatus = "Save failed: " + ex.Message;
        }
    }

    /// <summary>
    /// One Test for both backends: save what is on screen, then ask the active backend for a
    /// one-word reply. For Local that means the model the page names, with the options the page
    /// shows — loaded first if nothing is, reloaded if something else (or the same file with other
    /// options) is, since a test of the previous load says nothing about these settings.
    /// </summary>
    [RelayCommand]
    private async Task TestLlmAsync()
    {
        if (IsTesting) return;
        IsTesting = true;
        LlmStatus = "Testing…";
        try
        {
            var s = CollectLlm();
            LlmSettingsStore.Save(s);
            string label;
            var loadNote = "";
            if (s.ActiveBackend == LlmActiveBackend.Local)
            {
                if (SelectedModel is null) { LlmStatus = "Not configured: pick a local model."; return; }
                var path = LlmModelLocator.ResolveExistingOrTarget(SelectedModel.Entry);
                if (!File.Exists(path)) { LlmStatus = "The model file is not on disk — Download it first."; return; }
                label = $"Local / {SelectedModel.Entry.DisplayName} ({s.Backend})";
                if (!LoadedAsConfigured(s, path))
                {
                    if (LlmService.State == LlmServiceState.Loaded) await LlmService.UnloadAsync();
                    LlmStatus = $"Testing… loading {SelectedModel.Entry.DisplayName} ({s.Backend})";
                    var load = System.Diagnostics.Stopwatch.StartNew();
                    await LlmService.LoadAsync(s, path);
                    loadNote = $" · loaded in {load.ElapsedMilliseconds} ms";
                    LocalStatus = "Loaded · AI mode can use the local model now.";
                }
            }
            else
            {
                var model = s.ResolveExternalModel();
                if (s.CreateExternalProvider() is null || string.IsNullOrEmpty(model))
                {
                    LlmStatus = "Not configured: pick a provider and a model" + (s.External.Provider == ExternalProviderNames.OpenAI ? " and enter the API key." : ".");
                    return;
                }
                label = $"{s.External.Provider} / {model}";
            }

            var timeout = TimeSpan.FromSeconds(s.ActiveBackend == LlmActiveBackend.Local ? 120 : 45);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var reply = await Task.Run(async () =>
            {
                await using var session = LlmGateway.OpenSession();
                using var cts = new CancellationTokenSource(timeout);
                return await session.SendAsync("Reply with exactly the word OK.", cts.Token);
            });
            var preview = reply.Trim();
            if (preview.Length > 80) preview = preview[..80] + "…";
            LlmStatus = $"OK · {label} answered in {sw.ElapsedMilliseconds} ms{loadNote}: \"{preview}\"";
        }
        catch (Exception ex)
        {
            LlmStatus = $"Test failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>The local engine is up with this file and the options that shape a load.</summary>
    private static bool LoadedAsConfigured(LlmRuntimeSettings s, string path)
    {
        if (LlmService.State != LlmServiceState.Loaded || LlmService.Llm is null) return false;
        var c = LlmService.CurrentSettings;
        return string.Equals(LlmService.LoadedModelPath, path, StringComparison.OrdinalIgnoreCase)
            && c.Backend == s.Backend && c.ContextSize == s.ContextSize
            && c.GpuLayerCount == s.GpuLayerCount && c.VulkanDeviceIndex == s.VulkanDeviceIndex;
    }

    [RelayCommand]
    private async Task DownloadModelAsync()
    {
        if (SelectedModel is null || IsDownloading) return;
        var entry = SelectedModel.Entry;
        var target = LlmModelLocator.UserPathFor(entry);
        if (File.Exists(LlmModelLocator.ResolveExistingOrTarget(entry)))
        {
            LocalStatus = "Already downloaded.";
            return;
        }
        IsDownloading = true;
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
                LocalStatus = $"Downloading {entry.DisplayName}: {p.BytesReceived / 1048576.0:0} / {p.TotalBytes / 1048576.0:0} MB · {p.BytesPerSecond / 1048576.0:0.0} MB/s");
            await LlmModelDownloader.DownloadAsync(entry.DownloadUrl, target, progress);
            LocalStatus = "Download complete.";
            RefreshModelStatus();
        }
        catch (Exception ex)
        {
            LocalStatus = "Download failed: " + ex.Message;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private async Task LoadModelAsync()
    {
        if (!LocalSupported || SelectedModel is null) return;
        var s = CollectLlm();
        LlmSettingsStore.Save(s);
        var path = LlmModelLocator.ResolveExistingOrTarget(SelectedModel.Entry);
        if (!File.Exists(path))
        {
            LocalStatus = "The model file is not on disk — download it first.";
            return;
        }
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LocalStatus = $"Loading {SelectedModel.Entry.DisplayName} ({s.Backend})…";
            await LlmService.LoadAsync(s, path);
            LocalStatus = $"Loaded in {sw.ElapsedMilliseconds} ms · AI mode can use the local model now.";
        }
        catch (Exception ex)
        {
            LocalStatus = "Load failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task UnloadModelAsync()
    {
        try
        {
            await LlmService.UnloadAsync();
            LocalStatus = "Unloaded.";
        }
        catch (Exception ex)
        {
            LocalStatus = "Unload failed: " + ex.Message;
        }
    }

    // ═══ CLI definitions ════════════════════════════════════════════════════

    public ObservableCollection<CliDefinitionItem> CliDefinitions { get; } = new();
    [ObservableProperty] private CliDefinitionItem? _selectedCli;
    [ObservableProperty] private string _cliStatus = "";
    public bool SshEditable { get; }

    public bool CanDeleteCli => SelectedCli is { IsBuiltIn: false, Id: > 0 };

    private IReadOnlyList<string>? _shellChoices;
    /// <summary>The shells on this machine (pwsh only when installed, zsh only where it exists) for the picker.</summary>
    public IReadOnlyList<string> ShellChoices => _shellChoices ??= SafeDetectShells();

    private static IReadOnlyList<string> SafeDetectShells()
    {
        try { return InstalledShells.Detect(); }
        catch (Exception ex) { AppLogger.Log($"[Settings] shell detection failed | {ex.Message}"); return []; }
    }

    partial void OnSelectedCliChanged(CliDefinitionItem? oldValue, CliDefinitionItem? newValue)
    {
        OnPropertyChanged(nameof(CanDeleteCli));

        // The tool a definition drives is read out of its text, and that text is being
        // edited right here — type "codex" into a new definition's arguments and the
        // install panel should appear without reselecting the row.
        if (oldValue is not null) oldValue.PropertyChanged -= OnSelectedCliFieldChanged;
        if (newValue is not null) newValue.PropertyChanged += OnSelectedCliFieldChanged;
        RefreshCliTool();
    }

    private void OnSelectedCliFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CliDefinitionItem.ExePath) or nameof(CliDefinitionItem.Arguments))
            RefreshCliTool();
    }

    // ═══ Agent CLI install (Claude / Codex / AgentOne) ═══════════════════════
    //
    // A built-in definition is a shell plus an agent command, so the row can be perfectly
    // valid while the agent itself was never installed — the tab then opens and prints
    // "claude : The term 'claude' is not recognized", which reads as a broken app. This
    // panel answers "is it there?" and, when it is not, fetches it — with npm, or with the
    // vendor script for a tool that is not on npm. AgentCliTools holds every rule; this is only
    // its screen.

    [ObservableProperty] private string _cliToolStatus = "";
    [ObservableProperty] private string _installLog = "";
    [ObservableProperty] private bool _isInstallingTool;

    private AgentCliTool? _cliTool;

    /// <summary>The agent CLI the selected definition drives, or null for a plain shell.</summary>
    public AgentCliTool? CliTool
    {
        get => _cliTool;
        private set
        {
            if (ReferenceEquals(_cliTool, value)) return;
            _cliTool = value;
            OnPropertyChanged(nameof(CliTool));
            OnPropertyChanged(nameof(ShowCliTool));
            OnPropertyChanged(nameof(CliToolName));
            OnPropertyChanged(nameof(CanInstallCliTool));
        }
    }

    public bool ShowCliTool => CliTool is not null;
    public bool HasInstallLog => InstallLog.Length > 0;
    public string CliToolName => CliTool?.Name ?? "";
    public bool CanInstallCliTool => CliTool is not null && !IsInstallingTool;

    partial void OnIsInstallingToolChanged(bool value) => OnPropertyChanged(nameof(CanInstallCliTool));
    partial void OnInstallLogChanged(string value) => OnPropertyChanged(nameof(HasInstallLog));

    private void RefreshCliTool()
    {
        var tool = AgentCliTools.Match(SelectedCli?.ExePath, SelectedCli?.Arguments);
        var same = ReferenceEquals(tool, CliTool);
        CliTool = tool;
        if (tool is null) { CliToolStatus = ""; return; }
        if (!same) InstallLog = "";
        ProbeCliTool(tool);
    }

    private void ProbeCliTool(AgentCliTool tool)
    {
        try
        {
            var state = AgentCliTools.Probe(tool);
            CliToolStatus = state switch
            {
                { Installed: true, OnProcessPath: true } => $"{tool.Name} is installed · {state.ResolvedPath}",
                // Found, but this process started before it was on PATH — the tabs it
                // launches inherit that stale PATH, so saying "installed" here would be
                // followed by a tab that cannot find the command.
                { Installed: true } => $"{tool.Name} is installed at {state.ResolvedPath}, but this app started before it was on PATH — restart AgentZero so new tabs can find it.",
                _ => $"{tool.Name} was not found on PATH. Install it below, or see {tool.DocsUrl}",
            };
        }
        catch (Exception ex)
        {
            CliToolStatus = $"Could not check for {tool.Name}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CheckCliTool()
    {
        if (CliTool is { } tool) ProbeCliTool(tool);
    }

    [RelayCommand]
    private async Task InstallCliToolAsync()
    {
        if (CliTool is not { } tool || IsInstallingTool) return;

        var plan = AgentCliTools.PlanInstall(tool);
        InstallLog = plan.CanRun ? "$ " + plan.CommandLine + "\n" : plan.Problem + "\n";
        if (!plan.CanRun)
        {
            CliToolStatus = plan.Problem!;
            return;
        }

        IsInstallingTool = true;
        CliToolStatus = $"Installing {tool.Name}…";
        AppLogger.Log($"[Settings] agent CLI install | tool={tool.Name} cmd={plan.CommandLine}");
        try
        {
            // The installer reports from its own threads; every line is posted to the UI
            // thread rather than appended from there.
            var exit = await AgentCliTools.RunInstallAsync(plan,
                line => Dispatcher.UIThread.Post(() => AppendInstallLog(line)));

            if (exit == 0)
            {
                ProbeCliTool(tool);
                AppendInstallLog($"— {plan.Installer} finished.");
            }
            else
            {
                CliToolStatus = $"Installing {tool.Name} failed (exit {exit}). The log below is {plan.Installer}'s own output; {tool.DocsUrl} has the manual steps.";
            }
            AppLogger.Log($"[Settings] agent CLI install done | tool={tool.Name} exit={exit}");
        }
        catch (Exception ex)
        {
            CliToolStatus = $"Installing {tool.Name} failed: {ex.Message}";
        }
        finally
        {
            IsInstallingTool = false;
        }
    }

    /// <summary>
    /// Keep the tail of the installer's output. An installer that redraws a progress bar
    /// by repeating the line would otherwise grow the log by thousands of near-identical
    /// lines during one download.
    /// </summary>
    private void AppendInstallLog(string line)
    {
        const int keepLines = 200;
        var text = InstallLog + line + "\n";
        var lines = text.Split('\n');
        if (lines.Length > keepLines) text = string.Join("\n", lines[^keepLines..]);
        InstallLog = text;
    }

    private void LoadCli()
    {
        var keepId = SelectedCli?.Id;
        CliDefinitions.Clear();
        try
        {
            using var db = new AppDbContext();
            foreach (var d in db.CliDefinitions.AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Id).ToList())
            {
                // Definitions that cannot run here (.exe on macOS) stay in the database
                // for the Windows host and out of this list.
                if (!TerminalLaunchPlanner.IsAvailableOnThisOs(d)) continue;
                CliDefinitions.Add(new CliDefinitionItem(d));
            }
        }
        catch (Exception ex)
        {
            CliStatus = "Could not read the definitions: " + ex.Message;
        }
        SelectedCli = CliDefinitions.FirstOrDefault(c => c.Id == keepId) ?? CliDefinitions.FirstOrDefault();
    }

    [RelayCommand]
    private void NewCli()
    {
        var item = new CliDefinitionItem(new CliDefinition { Name = "New CLI", ExePath = OperatingSystem.IsWindows() ? "powershell.exe" : Agent.Common.Platform.PosixShell.Current.Default() });
        CliDefinitions.Add(item);
        SelectedCli = item;
        CliStatus = "Fill in the definition and click Save.";
    }

    [RelayCommand]
    private void SaveCli()
    {
        var item = SelectedCli;
        if (item is null) return;
        if (item.Validate() is { } problem)
        {
            CliStatus = problem;
            return;
        }
        try
        {
            using var db = new AppDbContext();
            CliDefinition entity;
            if (item.Id == 0)
            {
                entity = new CliDefinition { SortOrder = db.CliDefinitions.Any() ? db.CliDefinitions.Max(d => d.SortOrder) + 1 : 0 };
                item.ApplyTo(entity, SshPasswordVault.Protect);
                db.CliDefinitions.Add(entity);
            }
            else
            {
                entity = db.CliDefinitions.Find(item.Id) ?? throw new InvalidOperationException("The definition no longer exists.");
                item.ApplyTo(entity, SshPasswordVault.Protect);
            }
            db.SaveChanges();
            CliStatus = $"Saved '{entity.Name}'.";
            AppLogger.Log($"[Settings] CLI definition saved | id={entity.Id} name={entity.Name} exe={entity.ExePath}");
            var id = entity.Id;
            LoadCli();
            SelectedCli = CliDefinitions.FirstOrDefault(c => c.Id == id);
            CliDefinitionsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            CliStatus = "Save failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void DeleteCli()
    {
        var item = SelectedCli;
        if (item is null) return;
        if (item.IsBuiltIn)
        {
            CliStatus = "Built-in definitions cannot be deleted.";
            return;
        }
        if (item.Id == 0)
        {
            CliDefinitions.Remove(item);
            SelectedCli = CliDefinitions.FirstOrDefault();
            return;
        }
        try
        {
            using var db = new AppDbContext();
            var entity = db.CliDefinitions.Find(item.Id);
            if (entity is not null)
            {
                db.CliDefinitions.Remove(entity);
                db.SaveChanges();
            }
            CliStatus = $"Deleted '{item.Name}'.";
            LoadCli();
            CliDefinitionsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            CliStatus = "Delete failed: " + ex.Message;
        }
    }

    [RelayCommand] private void MoveUp() => Move(-1);
    [RelayCommand] private void MoveDown() => Move(+1);

    private void Move(int delta)
    {
        var item = SelectedCli;
        if (item is null || item.Id == 0) return;
        var i = CliDefinitions.IndexOf(item);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= CliDefinitions.Count) return;
        var other = CliDefinitions[j];
        if (other.Id == 0) return;
        try
        {
            using var db = new AppDbContext();
            var a = db.CliDefinitions.Find(item.Id);
            var b = db.CliDefinitions.Find(other.Id);
            if (a is null || b is null) return;
            // Swap; equal sort orders (legacy rows) are separated by position.
            var sa = a.SortOrder;
            var sb = b.SortOrder;
            if (sa == sb) { sa = i; sb = j; }
            a.SortOrder = sb;
            b.SortOrder = sa;
            db.SaveChanges();
            LoadCli();
            SelectedCli = CliDefinitions.FirstOrDefault(c => c.Id == item.Id);
            CliDefinitionsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            CliStatus = "Reorder failed: " + ex.Message;
        }
    }

    // ═══ Terminal appearance ════════════════════════════════════════════════

    public IReadOnlyList<string> ThemeNames { get; } = TerminalThemeCatalog.Names;
    [ObservableProperty] private string _fontFamily = "";

    // ── Font picker: the installed families, so the stack is chosen rather than typed ──
    private IReadOnlyList<InstalledFont> _installedFonts = [];
    private bool _syncingFont;

    /// <summary>What the picker lists: monospace families only unless <see cref="ShowAllFonts"/>.</summary>
    [ObservableProperty] private IReadOnlyList<InstalledFont> _fontChoices = [];
    [ObservableProperty] private InstalledFont? _selectedFont;
    /// <summary>A proportional font breaks the terminal grid, so the list starts monospace-only.</summary>
    [ObservableProperty] private bool _showAllFonts;
    [ObservableProperty] private string _fontListStatus = "Loading installed fonts…";

    private bool _fontsRequested;

    /// <summary>The Settings page came into view — Terminal is the first section, so the font
    /// list is read here rather than when the view model is built at startup.</summary>
    public void NotifyShown()
    {
        if (IsTerminal) EnsureInstalledFonts();
        if (IsLlm) EnsureExternalModels();
    }

    /// <summary>Reads the font list the first time the Terminal page opens — not at startup,
    /// since measuring every family loads every typeface.</summary>
    private void EnsureInstalledFonts()
    {
        if (_fontsRequested) return;
        _fontsRequested = true;
        _ = LoadInstalledFontsAsync();
    }

    private async Task LoadInstalledFontsAsync()
    {
        var fonts = await InstalledFontCatalog.LoadAsync().ConfigureAwait(true);
        _installedFonts = fonts;
        RefreshFontChoices();
    }

    private void RefreshFontChoices()
    {
        var primary = TerminalFontStack.Primary(FontFamily);
        // The font in use stays listed even when the filter would hide it — a picker that
        // cannot show the current value reads as "nothing is set".
        FontChoices = _installedFonts
            .Where(f => ShowAllFonts || f.IsMonospace || string.Equals(f.Name, primary, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var mono = _installedFonts.Count(f => f.IsMonospace);
        FontListStatus = _installedFonts.Count == 0
            ? "Installed fonts could not be read — edit the stack below."
            : ShowAllFonts ? $"{_installedFonts.Count} fonts ({mono} monospace)" : $"{mono} monospace fonts";
        SyncSelectedFont();
    }

    private void SyncSelectedFont()
    {
        var primary = TerminalFontStack.Primary(FontFamily);
        _syncingFont = true;
        try { SelectedFont = FontChoices.FirstOrDefault(f => string.Equals(f.Name, primary, StringComparison.OrdinalIgnoreCase)); }
        finally { _syncingFont = false; }
    }

    partial void OnShowAllFontsChanged(bool value) => RefreshFontChoices();

    partial void OnSelectedFontChanged(InstalledFont? value)
    {
        if (_syncingFont || value is null) return;
        FontFamily = TerminalFontStack.WithPrimary(FontFamily, value.Name);
    }

    partial void OnFontFamilyChanged(string value) => SyncSelectedFont();
    [ObservableProperty] private decimal? _fontSize = 14;
    [ObservableProperty] private decimal? _lineHeight = 1.0m;
    [ObservableProperty] private bool _cursorBlink;
    [ObservableProperty] private bool _useWebGl;
    [ObservableProperty] private string _themeName = TerminalThemeCatalog.DefaultName;
    [ObservableProperty] private string _terminalStatus = "";

    /// <summary>Ask which earlier session to reopen when a Claude / Codex tab starts (<see cref="AgentResumeSettings"/>).</summary>
    [ObservableProperty] private bool _askResumeClaude = true;
    [ObservableProperty] private bool _askResumeCodex = true;

    /// <summary>Re-read the resume questions — the session dialog's "don't ask again" writes the file behind this page.</summary>
    public void ReloadResumeSettings()
    {
        var r = AgentResumeSettingsStore.Load();
        AskResumeClaude = r.AskClaude;
        AskResumeCodex = r.AskCodex;
    }

    private void LoadTerminal()
    {
        TerminalSettings s;
        try { s = TerminalSettingsStore.Load(); } catch { s = new TerminalSettings(); }
        FontFamily = s.FontFamily;
        FontSize = s.FontSize;
        LineHeight = (decimal)s.LineHeight;
        CursorBlink = s.CursorBlink;
        UseWebGl = s.UseWebGlRenderer;
        ThemeName = TerminalThemeCatalog.IsPreset(s.ThemeName) ? s.ThemeName : TerminalThemeCatalog.DefaultName;
        ReloadResumeSettings();
    }

    /// <summary>Clamp and default the edited fields into a settings object (pure — the tests pin it).</summary>
    public static TerminalSettings BuildTerminalSettings(string? fontFamily, int fontSize, double lineHeight, bool cursorBlink, bool useWebGl, string? themeName)
    {
        var s = new TerminalSettings
        {
            FontSize = Math.Clamp(fontSize, TerminalSettings.MinFontSize, TerminalSettings.MaxFontSize),
            LineHeight = Math.Clamp(lineHeight, 0.8, 2.0),
            CursorBlink = cursorBlink,
            UseWebGlRenderer = useWebGl,
            ThemeName = TerminalThemeCatalog.IsPreset(themeName) ? themeName! : TerminalThemeCatalog.DefaultName,
        };
        if (!string.IsNullOrWhiteSpace(fontFamily)) s.FontFamily = fontFamily.Trim();
        return s;
    }

    [RelayCommand]
    private void SaveTerminal()
    {
        try
        {
            var s = BuildTerminalSettings(FontFamily, (int)(FontSize ?? 14), (double)(LineHeight ?? 1.0m), CursorBlink, UseWebGl, ThemeName);
            TerminalSettingsStore.Save(s);
            AgentResumeSettingsStore.Save(new AgentResumeSettings { AskClaude = AskResumeClaude, AskCodex = AskResumeCodex });
            FontSize = s.FontSize;
            LineHeight = (decimal)s.LineHeight;
            FontFamily = s.FontFamily;
            ThemeName = s.ThemeName;
            TerminalStatus = $"Applied · {s.EffectiveFontFamily.Split(',')[0].Trim()} {s.EffectiveFontSize}px · {s.ThemeName}" + (UseWebGl ? " · WebGL for new terminals" : "");
            AppLogger.Log($"[Settings] terminal appearance saved | {TerminalStatus}");
            AppearanceChanged?.Invoke();
        }
        catch (Exception ex)
        {
            TerminalStatus = "Save failed: " + ex.Message;
        }
    }
}
