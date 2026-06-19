using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// Markdown 문서 뷰어 VM. Markdown.Avalonia가 네이티브로 렌더(WebView 불필요).
/// 파일 경로가 주어지면 로드, 없으면 기본 안내 문서를 표시.
/// </summary>
public partial class MarkdownViewModel : ObservableObject
{
    [ObservableProperty]
    private string _text;

    public MarkdownViewModel(string? filePath = null)
    {
        if (filePath is not null && File.Exists(filePath))
        {
            try { _text = File.ReadAllText(filePath); return; }
            catch { /* fall through to default */ }
        }
        _text = DefaultDoc;
    }

    private const string DefaultDoc = """
# AgentZero Lite — Cross-Platform (Avalonia)

이 문서는 **Markdown.Avalonia** 네이티브 렌더러로 표시됩니다 — WebView 의존성이
없어 Windows/macOS/Linux에서 동일하게 동작합니다.

## 이식 현황
- 채팅 (External LLM, REST)
- 터미널 (Porta.Pty, cross-platform PTY) + 멀티 탭
- 도킹 레이아웃 (Dock.Avalonia)
- 플랫폼 추상화: 단일 인스턴스 · 비밀 보호 · IPC

## 다음
- Mermaid/임의 HTML 렌더 (WebView 패키지)
- 음성 (AVFoundation), 로컬 LLM (Mac 네이티브)

> 코드 예시:
>
> ```bash
> dotnet build Project/AgentZeroAvalonia -r osx-arm64
> ```
""";
}
