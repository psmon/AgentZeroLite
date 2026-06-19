using System.Linq;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using AgentZeroAvalonia.Docking;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>메인 창 VM — 도킹 레이아웃 + 액티비티바/사이드바 명령 + 브랜딩 버전.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly DockFactory _factory = new();
    private int _terminalCount = 1; // 초기 레이아웃에 터미널 1 존재

    [ObservableProperty]
    private IRootDock? _layout;

    [ObservableProperty]
    private bool _sidebarExpanded = true;

    [ObservableProperty]
    private string _botStatus = "⚡ Bot: Inactive";

    /// <summary>타이틀바 브랜딩에 표시할 "vX.Y.Z" (InformationalVersion에서 +hash 제거).</summary>
    public string VersionLabel { get; }

    public MainWindowViewModel()
    {
        var layout = _factory.CreateLayout();
        _factory.InitLayout(layout);
        Layout = layout;

        var info = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0";
        var plus = info.IndexOf('+');
        VersionLabel = "v" + (plus >= 0 ? info[..plus] : info);
    }

    /// <summary>새 터미널 도큐먼트를 도킹 영역에 추가하고 활성화.</summary>
    [RelayCommand]
    private void NewTerminal()
    {
        if (_factory.Documents is not { } documents) return;
        var doc = new TerminalDocument(++_terminalCount);
        _factory.AddDockable(documents, doc);
        _factory.SetActiveDockable(doc);
        _factory.SetFocusedDockable(documents, doc);
    }

    /// <summary>지정 Id의 도큐먼트를 활성화(액티비티바에서 사용). 없으면 무시.</summary>
    private void Activate(string id)
    {
        if (_factory.Documents?.VisibleDockables is null) return;
        var doc = _factory.Documents.VisibleDockables.FirstOrDefault(d => d.Id == id);
        if (doc is not null) _factory.SetActiveDockable(doc);
    }

    [RelayCommand] private void ShowChat() => Activate("Chat");
    [RelayCommand] private void ShowNotebook() => Activate("Notebook");

    [RelayCommand]
    private void ToggleSidebar() => SidebarExpanded = !SidebarExpanded;
}
