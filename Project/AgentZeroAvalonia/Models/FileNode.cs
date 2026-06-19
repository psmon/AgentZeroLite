using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace AgentZeroAvalonia.Models;

/// <summary>
/// 파일 트리 노드. 깊이/개수 제한으로 거대한 트리를 방어하며 eager 로드한다.
/// (bin/obj/.git/node_modules 등 노이즈 디렉터리는 건너뜀)
/// </summary>
public sealed class FileNode
{
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    { "bin", "obj", ".git", ".vs", ".idea", "node_modules", ".nuget" };

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public ObservableCollection<FileNode> Children { get; } = new();

    public string Icon => IsDirectory ? "📁" : "📄";

    public FileNode(string path, bool isDirectory, int depth, int maxDepth)
    {
        FullPath = path;
        IsDirectory = isDirectory;
        Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(Name)) Name = path;

        if (isDirectory && depth < maxDepth)
            LoadChildren(depth, maxDepth);
    }

    private void LoadChildren(int depth, int maxDepth)
    {
        try
        {
            var dirs = Directory.EnumerateDirectories(FullPath)
                .Where(d => !Skip.Contains(Path.GetFileName(d)))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Take(500);
            foreach (var d in dirs)
                Children.Add(new FileNode(d, true, depth + 1, maxDepth));

            var files = Directory.EnumerateFiles(FullPath)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(1000);
            foreach (var f in files)
                Children.Add(new FileNode(f, false, depth + 1, maxDepth));
        }
        catch { /* 접근 거부 등은 무시 */ }
    }

    /// <summary>루트 디렉터리에서 트리 생성(기본 깊이 6).</summary>
    public static FileNode CreateRoot(string dir, int maxDepth = 6)
        => new(dir, Directory.Exists(dir), 0, maxDepth);
}
