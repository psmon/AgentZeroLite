using System.Collections.ObjectModel;
using Agent.Common;
using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Agent.Common.Llm;
using Agent.Common.Llm.Providers;
using Agent.Common.Module;
using Agent.Common.Security;
using Agent.Common.Services;
using AgentZeroAvalonia.Security;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace AgentZeroAvalonia.ViewModels;

public enum SettingsSection { Llm, Cli, Terminal }

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
    [ObservableProperty] private SettingsSection _section = SettingsSection.Llm;

    public bool IsLlm => Section == SettingsSection.Llm;
    public bool IsCli => Section == SettingsSection.Cli;
    public bool IsTerminal => Section == SettingsSection.Terminal;

    partial void OnSectionChanged(SettingsSection value)
    {
        OnPropertyChanged(nameof(IsLlm));
        OnPropertyChanged(nameof(IsCli));
        OnPropertyChanged(nameof(IsTerminal));
    }

    [RelayCommand] private void ShowLlm() => Section = SettingsSection.Llm;
    [RelayCommand] private void ShowCli() => Section = SettingsSection.Cli;
    [RelayCommand] private void ShowTerminal() => Section = SettingsSection.Terminal;

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
    }

    partial void OnUseExternalChanged(bool value) { if (value) UseLocal = false; }
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
            LocalStatus = LlmStatus;
            AppLogger.Log($"[Settings] LLM saved | {LlmStatus}");
        }
        catch (Exception ex)
        {
            LlmStatus = "Save failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task TestExternalAsync()
    {
        if (IsTesting) return;
        IsTesting = true;
        LlmStatus = "Testing…";
        try
        {
            var s = CollectLlm();
            LlmSettingsStore.Save(s);
            var provider = s.CreateExternalProvider();
            var model = s.ResolveExternalModel();
            if (provider is null || string.IsNullOrEmpty(model))
            {
                LlmStatus = "Not configured: pick a provider and a model" + (s.External.Provider == ExternalProviderNames.OpenAI ? " and enter the API key." : ".");
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var reply = await Task.Run(async () =>
            {
                await using var session = LlmGateway.OpenSession();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                return await session.SendAsync("Reply with exactly the word OK.", cts.Token);
            });
            var preview = reply.Trim();
            if (preview.Length > 80) preview = preview[..80] + "…";
            LlmStatus = $"OK · {s.External.Provider} / {model} answered in {sw.ElapsedMilliseconds} ms: \"{preview}\"";
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

    // ═══ Agent CLI install (Claude / Codex) ══════════════════════════════════
    //
    // A built-in definition is a shell plus an agent command, so the row can be perfectly
    // valid while the agent itself was never installed — the tab then opens and prints
    // "claude : The term 'claude' is not recognized", which reads as a broken app. This
    // panel answers "is it there?" and, when it is not, fetches it the way each platform
    // publishes: winget on Windows, npm elsewhere. AgentCliTools holds every rule; this
    // is only its screen.

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
                AppendInstallLog($"— {plan.Exe} finished.");
            }
            else
            {
                CliToolStatus = $"Installing {tool.Name} failed (exit {exit}). The log below is {plan.Exe}'s own output; {tool.DocsUrl} has the manual steps.";
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
    /// Keep the tail of the installer's output. winget redraws a progress bar by
    /// repeating the line, so an unbounded log grows by thousands of near-identical
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
        var item = new CliDefinitionItem(new CliDefinition { Name = "New CLI", ExePath = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/zsh" });
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
    [ObservableProperty] private decimal? _fontSize = 14;
    [ObservableProperty] private decimal? _lineHeight = 1.0m;
    [ObservableProperty] private bool _cursorBlink;
    [ObservableProperty] private bool _useWebGl;
    [ObservableProperty] private string _themeName = TerminalThemeCatalog.DefaultName;
    [ObservableProperty] private string _terminalStatus = "";

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
