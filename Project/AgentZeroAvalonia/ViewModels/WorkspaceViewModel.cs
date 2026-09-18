using System.Collections.ObjectModel;
using Agent.Common.Module;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// A workspace — a folder and the terminal tabs opened in it (M0035). It is the
/// <see cref="ICliGroupInfo"/> that <c>CliWorkspacePersistence</c> saves and the CLI
/// catalog lists, so the same rows the WPF host writes come back here unchanged.
/// </summary>
public partial class WorkspaceViewModel : ObservableObject, ICliGroupInfo
{
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private TerminalTabViewModel? _activeTab;

    public string DirectoryPath { get; }
    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = new();

    public Action<WorkspaceViewModel>? ActivateRequested { get; set; }
    public Action<WorkspaceViewModel>? RemoveRequested { get; set; }

    public WorkspaceViewModel(string directoryPath, string displayName)
    {
        DirectoryPath = directoryPath;
        _displayName = displayName;
    }

    IReadOnlyList<IConsoleTabInfo> ICliGroupInfo.TabsView => Tabs;
    public int ActiveTabIndex => ActiveTab is null ? 0 : Math.Max(0, Tabs.IndexOf(ActiveTab));

    /// <summary>Split layout arrives in M0036; until then the workspace is one pane.</summary>
    public string? DockLayoutJson => null;

    partial void OnActiveTabChanged(TerminalTabViewModel? oldValue, TerminalTabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null) newValue.IsActive = true;
    }

    [RelayCommand] private void Activate() => ActivateRequested?.Invoke(this);
    [RelayCommand] private void Remove() => RemoveRequested?.Invoke(this);
}
