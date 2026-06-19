using System.Collections.Generic;
using Dock.Model.Core;
using Dock.Model.Controls;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace AgentZeroAvalonia.Docking;

/// <summary>
/// 초기 도킹 레이아웃 팩토리. RootDock → DocumentDock(채팅/터미널 도큐먼트).
/// AvalonDock(WPF)의 LayoutDocumentPane 역할을 Dock.Avalonia로 대체한다.
/// </summary>
public sealed class DockFactory : Factory
{
    /// <summary>도큐먼트 추가/활성화에 쓰도록 보관(새 터미널 탭 추가용).</summary>
    public IDocumentDock? Documents { get; private set; }

    public override IRootDock CreateLayout()
    {
        var chat = new ChatDocument();
        var terminal = new TerminalDocument(1);
        var notebook = new NotebookDocument();
        var clipboard = new ClipboardDocument();
        var settings = new SettingsDocument();

        var documents = new DocumentDock
        {
            Id = "Documents",
            Title = "Documents",
            IsCollapsable = false,
            CanCreateDocument = false,
            VisibleDockables = CreateList<IDockable>(chat, terminal, notebook, clipboard, settings),
            // 시작 활성 탭은 채팅(비PTY). 터미널을 기본 활성으로 두면 IDE 디버거의
            // 통합 콘솔이 ConPTY를 가로채는 환경(Rider/VS 기본)에서 시작 시 멈춘다.
            // 터미널은 탭 클릭 시 spawn — 상호작용은 외부 콘솔 권장(README/PORTING 참조).
            ActiveDockable = chat,
        };
        Documents = documents;

        var root = CreateRootDock();
        root.Id = "Root";
        root.Title = "Root";
        root.VisibleDockables = CreateList<IDockable>(documents);
        root.ActiveDockable = documents;
        root.DefaultDockable = documents;
        return root;
    }
}
