using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>Which full-area page is showing — the WPF host's activity-bar pages, minus what is out of scope.</summary>
public enum AppPage
{
    Terminals,
    Settings,
}

/// <summary>
/// The shell (M0034): activity bar, workspace sidebar, the page area, the AgentBot
/// pane toggle and the status bar. Terminals, bot and settings each get their own view
/// model in later missions; this one only routes.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private AppPage _page = AppPage.Terminals;

    [ObservableProperty]
    private bool _sidebarExpanded = true;

    [ObservableProperty]
    private bool _botVisible;

    [ObservableProperty]
    private string _botStatus = "Bot: not started";

    [ObservableProperty]
    private string _statusText = "Ready";

    public string VersionLabel { get; }

    public MainWindowViewModel()
    {
        var info = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = info.IndexOf('+');
        VersionLabel = "v" + (plus >= 0 ? info[..plus] : info);
    }

    public bool IsTerminalsPage => Page == AppPage.Terminals;
    public bool IsSettingsPage => Page == AppPage.Settings;

    partial void OnPageChanged(AppPage value)
    {
        OnPropertyChanged(nameof(IsTerminalsPage));
        OnPropertyChanged(nameof(IsSettingsPage));
    }

    [RelayCommand] private void ShowTerminals() => Page = AppPage.Terminals;
    [RelayCommand] private void ShowSettings() => Page = Page == AppPage.Settings ? AppPage.Terminals : AppPage.Settings;
    [RelayCommand] private void ToggleSidebar() => SidebarExpanded = !SidebarExpanded;
    [RelayCommand] private void ToggleBot() => BotVisible = !BotVisible;
}
