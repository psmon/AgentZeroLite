using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using AgentZeroAvalonia.Docking;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>메인 창 VM — 도킹 레이아웃 보유 + 새 터미널 도큐먼트 추가.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly DockFactory _factory = new();
    private int _terminalCount = 1; // 초기 레이아웃에 터미널 1 존재

    [ObservableProperty]
    private IRootDock? _layout;

    public MainWindowViewModel()
    {
        var layout = _factory.CreateLayout();
        _factory.InitLayout(layout);
        Layout = layout;
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
}
