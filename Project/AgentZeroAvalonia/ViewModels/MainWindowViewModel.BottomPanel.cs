using Agent.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>What the bottom panel is showing.</summary>
public enum BottomTab { Bot, Code }

/// <summary>
/// The bottom panel as VS Code's: tabs over one docked area — 🤖 BOT (M0041) and 📄 CODE, the
/// code view (the WPF host's NOTE file tree + document viewer, read-only). Each tab is open or
/// closed and docked or floating on its own; the dock shows when either is open and docked, and
/// the pane chrome (⧉ float · ▣ maximize · ✕ close) acts on the tab in front. Height and
/// maximize are the panel's, shared by both tabs.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The code view (workspace files, read-only, VS Code grammars).</summary>
    public CodeViewModel Code { get; } = new();

    [ObservableProperty] private bool _codeVisible;
    [ObservableProperty] private bool _codeDocked = true;
    [ObservableProperty] private BottomTab _bottomTab = BottomTab.Bot;

    /// <summary>Raised with "should the code window be open"; the view opens/closes it.</summary>
    public event Action<bool>? CodeFloatRequested;

    public bool CodeDockVisible => CodeVisible && CodeDocked;

    /// <summary>The docked area exists when either tab is open and docked.</summary>
    public bool BottomDockVisible => BotDockVisible || CodeDockVisible;

    /// <summary>Which tab's content is drawn: the selected one, or the only one docked.</summary>
    public bool ShowBotInDock => BotDockVisible && (BottomTab == BottomTab.Bot || !CodeDockVisible);
    public bool ShowCodeInDock => CodeDockVisible && (BottomTab == BottomTab.Code || !BotDockVisible);

    /// <summary>
    /// ⎇ GIT LOG sits beside 📄 CODE and shares its pane: it is the code panel showing history
    /// instead of files (<see cref="CodeViewModel.ShowGitLog"/>), so it floats and closes with it.
    /// </summary>
    public bool CodeFilesTabSelected => ShowCodeInDock && Code.ShowFilesPane;
    public bool GitLogTabSelected => ShowCodeInDock && Code.ShowGitLog;

    /// <summary>± DIFF, the third face of the code panel (M0042): the working tree's changes, or the AI mode's writes.</summary>
    public bool DiffTabSelected => ShowCodeInDock && Code.ShowDiff;

    private void WireCodeTabs()
    {
        Code.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CodeViewModel.ShowGitLog) or nameof(CodeViewModel.ShowDiff)) RaiseCodeTabs();
        };
        // Review notes go to whoever made the change: the agent in the terminal, or the bot.
        Code.Diff.SendToTerminal = SendReviewToActiveTerminal;
        Code.Diff.AskBot = text =>
        {
            BotVisible = true;
            Bot.AttachActors();
            Bot.AskAi(text);
        };
    }

    private string? SendReviewToActiveTerminal(string text)
    {
        var tab = ActiveWorkspace?.ActiveTab;
        if (tab?.Session is not { } session || !tab.IsTerminalStarted) return "The active tab has no running terminal.";
        _ = Agent.Common.Agents.TerminalTextSender.SendAsync(session, text);
        AppLogger.Log($"[DiffReview] sent {text.Length} chars to {ActiveWorkspace?.DisplayName}/{tab.Title}");
        return null;
    }

    private void RaiseCodeTabs()
    {
        OnPropertyChanged(nameof(CodeFilesTabSelected));
        OnPropertyChanged(nameof(GitLogTabSelected));
        OnPropertyChanged(nameof(DiffTabSelected));
    }

    partial void OnCodeVisibleChanged(bool value)
    {
        RaiseBottomPanel();
        CodeFloatRequested?.Invoke(value && !CodeDocked);
        if (value) Code.FollowWorkspace(ActiveWorkspace);
    }

    partial void OnCodeDockedChanged(bool value)
    {
        RaiseBottomPanel();
        CodeFloatRequested?.Invoke(CodeVisible && !value);
    }

    partial void OnBottomTabChanged(BottomTab value) => RaiseBottomPanel();

    private void RaiseBottomPanel()
    {
        OnPropertyChanged(nameof(CodeDockVisible));
        OnPropertyChanged(nameof(BottomDockVisible));
        OnPropertyChanged(nameof(ShowBotInDock));
        OnPropertyChanged(nameof(ShowCodeInDock));
        RaiseCodeTabs();
        if (!BottomDockVisible) BotMaximized = false;
    }

    /// <summary>
    /// 📄 on the activity bar / Ctrl+Alt+E: open the code view in front, or close it when it is
    /// already in front. A floating view is brought forward instead: its window has no owner, so
    /// clicking 📄 in the main window has just put the main window over it — "in front" can no
    /// longer be told, and closing what the person was looking for would be the wrong guess.
    /// It goes away through its own ↩ Dock back / close box, then ✕ on the tab.
    /// </summary>
    [RelayCommand]
    public void ToggleCode()
    {
        if (CodeVisible && !CodeDocked)
        {
            CodeFloatRequested?.Invoke(true);   // raise the window that is already open
            return;
        }
        if (CodeVisible && (ShowCodeInDock || !CodeDocked))
        {
            CodeVisible = false;
            return;
        }
        CodeVisible = true;
        BottomTab = BottomTab.Code;
    }

    [RelayCommand] private void SelectBotTab() => BottomTab = BottomTab.Bot;
    [RelayCommand]
    private void SelectCodeTab()
    {
        BottomTab = BottomTab.Code;
        Code.ShowGitLog = false;
        Code.ShowDiff = false;
    }

    [RelayCommand]
    private void SelectDiffTab()
    {
        BottomTab = BottomTab.Code;
        Code.ShowDiff = true;
    }

    [RelayCommand]
    private void SelectGitLogTab()
    {
        if (!Code.GitLog.IsAvailable) return;
        BottomTab = BottomTab.Code;
        Code.ShowGitLog = true;
    }

    [RelayCommand] public void EmbedCode() => CodeDocked = true;

    // ── pane chrome: acts on the tab in front ────────────────────────────────

    [RelayCommand]
    private void FloatFrontTab()
    {
        if (ShowCodeInDock) { CodeDocked = false; StatusText = "CODE FLOATING"; }
        else UndockBot();
    }

    [RelayCommand]
    private void CloseFrontTab()
    {
        if (ShowCodeInDock) CodeVisible = false;
        else CloseBot();
    }

    /// <summary>The CLI <c>code</c> verbs: open [path] · close · status. Returns the reply JSON.</summary>
    public async Task<string> CodeCommandAsync(string verb, string path)
    {
        switch (verb)
        {
            case "open":
                if (!CodeVisible) CodeVisible = true;
                BottomTab = BottomTab.Code;
                await Code.SetRootAsync(ActiveWorkspace?.DirectoryPath, ActiveWorkspace?.DisplayName);
                if (path.Length > 0 && await Code.OpenAsync(path) is { } error)
                    return Agent.Common.Platform.CliIpcProtocol.ErrorJson(error);
                break;
            case "close":
                CodeVisible = false;
                break;
            case "view":
                // Markdown: rendered (with mermaid) or its source.
                if (path is not ("preview" or "plain"))
                    return Agent.Common.Platform.CliIpcProtocol.ErrorJson("usage: code view preview|plain");
                Code.MarkdownPreview = path == "preview";
                break;
            case "tab":
                // The panel's faces: the files, the GIT LOG, or the DIFF (working tree, or the AI's writes).
                if (path is not ("files" or "gitlog" or "diff" or "ai"))
                    return Agent.Common.Platform.CliIpcProtocol.ErrorJson("usage: code tab files|gitlog|diff|ai");
                if (path == "gitlog" && !Code.GitLog.IsAvailable)
                    return Agent.Common.Platform.CliIpcProtocol.ErrorJson("git log unavailable: " + Code.GitLog.Note);
                if (path is "diff" or "ai")
                {
                    Code.Diff.Source = path == "ai" ? DiffSource.AiChanges : DiffSource.WorkingTree;
                    Code.ShowDiff = true;
                    await Code.Diff.LoadAsync();
                }
                else
                {
                    Code.ShowDiff = false;
                    Code.ShowGitLog = path == "gitlog";
                }
                break;
            case "status" or "":
                break;
            default:
                return Agent.Common.Platform.CliIpcProtocol.ErrorJson($"unknown code verb '{verb}'. Use: open [path], close, view, tab, status");
        }
        AppLogger.Log($"[Code] cli {verb} {path}");
        return Code.StatusJson(CodeVisible, CodeDocked);
    }
}
