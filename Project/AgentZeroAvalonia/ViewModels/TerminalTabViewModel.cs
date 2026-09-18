using Agent.Common.Data.Entities;
using Agent.Common.Module;
using Agent.Common.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// One terminal tab (M0035). The WPF <c>ConsoleTabInfo</c> as a view model: it is the
/// <see cref="IConsoleTabInfo"/> the CLI catalog and the actor binding read, and it
/// carries no view type — the view keeps the control keyed by this object.
/// </summary>
public partial class TerminalTabViewModel : ObservableObject, IConsoleTabInfo
{
    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isTerminalStarted;
    [ObservableProperty] private string _healthText = "";
    [ObservableProperty] private XtermTerminalSession? _session;

    public int CliDefinitionId { get; }

    /// <summary>The definition this tab launches; null when it was persisted with an id that no longer exists.</summary>
    public CliDefinition? Definition { get; }

    /// <summary>Set by the view once the PTY is spawned; the restart path relaunches it.</summary>
    public TerminalLaunchSpec? LaunchSpec { get; set; }

    /// <summary>The session id last handed to the actors, so a restart binds a fresh one (WPF <c>LastBoundSessionId</c>).</summary>
    public string? LastBoundSessionId { get; set; }

    /// <summary>Owner callbacks — the tab strip binds to the commands, the owner does the work.</summary>
    public Action<TerminalTabViewModel>? ActivateRequested { get; set; }
    public Action<TerminalTabViewModel>? CloseRequested { get; set; }

    public TerminalTabViewModel(string title, int cliDefinitionId, CliDefinition? definition)
    {
        _title = title;
        CliDefinitionId = cliDefinitionId;
        Definition = definition;
    }

    ITerminalSession? IConsoleTabInfo.Session => Session;

    [RelayCommand] private void Activate() => ActivateRequested?.Invoke(this);
    [RelayCommand] private void Close() => CloseRequested?.Invoke(this);
}
