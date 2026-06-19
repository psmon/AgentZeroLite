using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentZeroAvalonia.Models;

/// <summary>
/// 파일 트리 노드 — **지연 로딩**. 생성 시 자식을 재귀하지 않고, TreeViewItem이
/// 펼쳐질 때(IsExpanded=true) 한 단계만 로드한다. (eager 재귀는 큰 디렉터리에서
/// UI 스레드를 막아 앱이 멈췄던 원인 — 지연 로딩으로 해소.)
/// </summary>
public sealed partial class FileNode : ObservableObject
{
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    { "bin", "obj", ".git", ".vs", ".idea", "node_modules", ".nuget" };

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public ObservableCollection<FileNode> Children { get; } = new();

    public string Icon => IsDirectory ? "📁" : "📄";

    private bool _loaded;

    [ObservableProperty]
    private bool _isExpanded;

    public FileNode(string path, bool isDirectory)
    {
        FullPath = path;
        IsDirectory = isDirectory;
        Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(Name)) Name = path;

        // 디렉터리면 펼침 화살표가 보이도록 더미 자식 1개를 둔다(실제 로드는 펼칠 때).
        if (isDirectory) Children.Add(Placeholder);
    }

    private static readonly FileNode Placeholder = new("…", false);

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) EnsureLoaded();
    }

    /// <summary>펼침 시 1단계 자식 로드(1회).</summary>
    public void EnsureLoaded()
    {
        if (_loaded || !IsDirectory) return;
        _loaded = true;
        Children.Clear();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(FullPath)
                         .Where(d => !Skip.Contains(Path.GetFileName(d)))
                         .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).Take(2000))
                Children.Add(new FileNode(d, true));

            foreach (var f in Directory.EnumerateFiles(FullPath)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(5000))
                Children.Add(new FileNode(f, false));
        }
        catch { /* 접근 거부 등은 무시 */ }
    }

    /// <summary>루트 노드 생성 후 1단계만 펼쳐 로드.</summary>
    public static FileNode CreateRoot(string dir)
    {
        var root = new FileNode(dir, Directory.Exists(dir));
        root.IsExpanded = true; // 최상위 항목만 즉시 로드(1단계, 가벼움)
        return root;
    }
}
