using Dock.Model.Mvvm.Controls;
using AgentZeroAvalonia.ViewModels;

namespace AgentZeroAvalonia.Docking;

// 도킹 도큐먼트 VM — App.axaml의 DataTemplate이 타입으로 View를 매핑한다.
// ChatDocument → AgentChatView, TerminalDocument → TerminalView(자체 PTY),
// MarkdownDocument → MarkdownView(DataContext = Vm).

public sealed class ChatDocument : Document
{
    public ChatDocument() { Id = "Chat"; Title = "채팅"; }
}

/// <summary>Markdown 문서 뷰어 도큐먼트.</summary>
public sealed class MarkdownDocument : Document
{
    public MarkdownViewModel Vm { get; }

    public MarkdownDocument(string? filePath = null)
    {
        Vm = new MarkdownViewModel(filePath);
        Id = "Markdown";
        Title = "문서";
    }
}

/// <summary>노트북 — 파일트리 + 문서 뷰어(마크다운/텍스트/이미지).</summary>
public sealed class NotebookDocument : Document
{
    public NotebookViewModel Vm { get; } = new();

    public NotebookDocument()
    {
        Id = "Notebook";
        Title = "노트";
    }
}

/// <summary>설정 — External LLM + CLI 정의.</summary>
public sealed class SettingsDocument : Document
{
    public SettingsViewModel Vm { get; } = new();

    public SettingsDocument()
    {
        Id = "Settings";
        Title = "설정";
    }
}

/// <summary>터미널 도큐먼트 — 각 탭이 독립 TerminalControl(자체 PTY)을 호스팅.</summary>
public sealed class TerminalDocument : Document
{
    public TerminalDocument(int tab)
    {
        Id = $"Terminal{tab}";
        Title = $"터미널 {tab}";
    }
}
