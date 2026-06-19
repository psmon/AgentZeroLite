using System;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using AgentZeroAvalonia.Models;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// 노트북 — 워크스페이스 파일트리 + 문서 뷰어(마크다운/텍스트/이미지).
/// WPF의 NoteWindow(파일트리 + DocumentViewerPanel) 구성을 옮긴 것.
/// </summary>
public partial class NotebookViewModel : ObservableObject
{
    private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
    private static readonly string[] MarkdownExt = { ".md", ".markdown" };

    public ObservableCollection<FileNode> RootNodes { get; } = new();

    [ObservableProperty] private string _rootLabel = "";
    [ObservableProperty] private string _documentTitle = "파일을 선택하세요";
    [ObservableProperty] private string _markdownText = "";
    [ObservableProperty] private string _plainText = "";
    [ObservableProperty] private Bitmap? _image;
    // 표시 모드
    [ObservableProperty] private bool _isMarkdown;
    [ObservableProperty] private bool _isImage;
    [ObservableProperty] private bool _isText;

    private FileNode? _selectedNode;
    public FileNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value) && value is { IsDirectory: false })
                OpenFile(value.FullPath);
        }
    }

    public NotebookViewModel() => SetRoot(Environment.CurrentDirectory);

    public void SetRoot(string dir)
    {
        RootNodes.Clear();
        if (!Directory.Exists(dir)) return;
        RootNodes.Add(FileNode.CreateRoot(dir));
        RootLabel = dir;
    }

    private void OpenFile(string path)
    {
        DocumentTitle = Path.GetFileName(path);
        IsMarkdown = IsImage = IsText = false;
        MarkdownText = PlainText = "";
        Image?.Dispose();
        Image = null;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (Array.IndexOf(ImageExt, ext) >= 0)
            {
                Image = new Bitmap(path);
                IsImage = true;
            }
            else if (Array.IndexOf(MarkdownExt, ext) >= 0)
            {
                MarkdownText = File.ReadAllText(path);
                IsMarkdown = true;
            }
            else
            {
                // 텍스트/코드 — 최대 512KB 까지만 표시(바이너리/거대파일 방어)
                var info = new FileInfo(path);
                if (info.Length > 512 * 1024)
                {
                    PlainText = $"(파일이 너무 큽니다: {info.Length / 1024} KB — 미리보기 생략)";
                }
                else
                {
                    PlainText = File.ReadAllText(path);
                }
                IsText = true;
            }
        }
        catch (Exception ex)
        {
            PlainText = $"열기 실패: {ex.Message}";
            IsText = true;
        }
    }
}
