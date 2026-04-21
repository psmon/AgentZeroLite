using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentZeroWpf.UI.APP;

namespace AgentZeroWpf.UI.Components;

public partial class FileTreePanel : UserControl
{
    private string _rootPath = "";
    private HashSet<string> _gitIgnorePatterns = [];

    /// <summary>Fires when a file node is selected (click). Arg = full path.</summary>
    public event Action<string>? FileSelected;

    /// <summary>Fires when a new file is created. Arg = full path. (Should open in edit mode.)</summary>
    public event Action<string>? FileCreated;

    /// <summary>Fires when a clipboard copy happens inside this panel (for history tracking).</summary>
    public event Action<string, string>? ClipboardCopied; // content, source

    public void LoadRoot(string rootPath)
    {
        _rootPath = rootPath;
        txtRootName.Text = Path.GetFileName(rootPath);
        if (string.IsNullOrEmpty(txtRootName.Text)) txtRootName.Text = rootPath;

        _gitIgnorePatterns = LoadGitIgnorePatterns(rootPath);
        RefreshTree();
    }

    private void RefreshTree()
    {
        tvFiles.Items.Clear();
        if (!Directory.Exists(_rootPath)) return;

        var rootNode = CreateDirectoryNode(_rootPath, isRoot: true);
        if (rootNode != null)
        {
            // Add children directly to tree (skip root wrapper)
            foreach (var child in ((TreeViewItem)rootNode).Items.Cast<TreeViewItem>().ToList())
            {
                ((TreeViewItem)rootNode).Items.Remove(child);
                tvFiles.Items.Add(child);
            }
        }
    }

    private TreeViewItem? CreateDirectoryNode(string dirPath, bool isRoot = false)
    {
        var dirName = Path.GetFileName(dirPath);
        if (!isRoot && ShouldIgnore(dirName, isDirectory: true)) return null;

        var node = new TreeViewItem
        {
            Header = CreateNodeHeader(dirName, isDirectory: true),
            Tag = dirPath,
            IsExpanded = isRoot,
        };

        try
        {
            // Directories first, then files
            foreach (var sub in Directory.GetDirectories(dirPath).OrderBy(Path.GetFileName))
            {
                var child = CreateDirectoryNode(sub);
                if (child != null) node.Items.Add(child);
            }

            foreach (var file in Directory.GetFiles(dirPath).OrderBy(Path.GetFileName))
            {
                var fileName = Path.GetFileName(file);
                if (ShouldIgnore(fileName, isDirectory: false)) continue;

                var fileNode = new TreeViewItem
                {
                    Header = CreateNodeHeader(fileName, isDirectory: false),
                    Tag = file,
                };
                node.Items.Add(fileNode);
            }
        }
        catch (UnauthorizedAccessException) { /* skip inaccessible dirs */ }
        catch (IOException) { }

        return node;
    }

    private static StackPanel CreateNodeHeader(string name, bool isDirectory)
    {
        string icon = isDirectory ? "\uE8B7" : GetFileIcon(name);
        var iconColor = isDirectory ? "#F0FF00" : "#C8D6E5";

        var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = icon,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(iconColor)!),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        sp.Children.Add(new TextBlock
        {
            Text = name,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return sp;
    }

    private static string GetFileIcon(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".md" => "\uE8A5",       // Document
            ".pdf" => "\uE8A5",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico" => "\uEB9F", // Image
            ".cs" or ".java" or ".py" or ".js" or ".ts" or ".cpp" or ".c" or ".go"
                or ".rs" or ".rb" or ".swift" or ".kt" => "\uE943", // Code
            ".json" or ".xml" or ".yaml" or ".yml" or ".toml" => "\uE90F", // Settings
            ".sln" or ".csproj" => "\uE74C", // Project
            _ => "\uE7C3",            // Generic file
        };
    }

    // ── .gitignore ──

    private static HashSet<string> LoadGitIgnorePatterns(string rootPath)
    {
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "bin", "obj", "node_modules", ".vs", ".idea",
            "__pycache__", ".venv", "venv", ".next", "dist",
        };

        var gitIgnore = Path.Combine(rootPath, ".gitignore");
        if (!File.Exists(gitIgnore)) return patterns;

        foreach (var raw in File.ReadAllLines(gitIgnore))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
            // Strip trailing slash & leading slash
            var p = line.TrimEnd('/').TrimStart('/');
            if (!string.IsNullOrEmpty(p)) patterns.Add(p);
        }

        return patterns;
    }

    private bool ShouldIgnore(string name, bool isDirectory)
    {
        if (string.IsNullOrEmpty(name)) return true;
        if (name.StartsWith('.') && name != ".gitignore" && name != ".claude") return true; // hidden files/dirs

        foreach (var pattern in _gitIgnorePatterns)
        {
            // Simple name match
            if (string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase)) return true;

            // Wildcard extension match (e.g. *.log)
            if (pattern.StartsWith('*'))
            {
                var ext = pattern[1..]; // e.g. ".log"
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }

    // ── Context Menu Visibility ──

    public FileTreePanel()
    {
        InitializeComponent();
        tvFiles.ContextMenuOpening += OnContextMenuOpening;
    }

    private void OnContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        var path = GetSelectedPath();
        // "New Note" only enabled for directories
        mnuNewNote.IsEnabled = path != null && Directory.Exists(path);
    }

    // ── New Note ──

    private void OnNewNoteClick(object sender, RoutedEventArgs e)
    {
        var dirPath = GetSelectedDirPath();
        if (dirPath == null) return;

        var dlg = new NewNoteDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        var fileName = dlg.FileName;
        if (string.IsNullOrWhiteSpace(fileName)) return;

        var fullPath = Path.Combine(dirPath, fileName + ".md");
        if (File.Exists(fullPath))
        {
            MessageBox.Show($"'{fileName}.md' already exists.", "Duplicate",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        File.WriteAllText(fullPath, $"# {fileName}\n");
        InsertFileNode(dirPath, fullPath);
        FileCreated?.Invoke(fullPath);
    }

    /// <summary>Expand tree to the given file and select it. Expands all ancestor directories.</summary>
    public void SelectFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;

        // Build ancestor chain from root to file
        var relative = Path.GetRelativePath(_rootPath, filePath);
        if (relative.StartsWith("..")) return; // outside root

        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        ItemCollection currentItems = tvFiles.Items;
        TreeViewItem? target = null;

        // Walk each segment, expanding directories along the way
        var currentPath = _rootPath;
        for (int i = 0; i < parts.Length; i++)
        {
            currentPath = Path.Combine(currentPath, parts[i]);
            var node = FindNodeByPath(currentItems, currentPath);
            if (node == null) break;

            if (i < parts.Length - 1)
            {
                // Directory — expand it
                node.IsExpanded = true;
                node.UpdateLayout(); // ensure children are generated
                currentItems = node.Items;
            }
            else
            {
                target = node;
            }
        }

        if (target != null)
        {
            target.IsSelected = true;
            target.BringIntoView();
        }
    }

    /// <summary>Insert a single file node into the correct parent without full tree reload.</summary>
    private void InsertFileNode(string parentDir, string filePath)
    {
        var parentNode = FindNodeByPath(tvFiles.Items, parentDir);
        if (parentNode == null) { RefreshTree(); return; }

        var fileName = Path.GetFileName(filePath);
        var newNode = new TreeViewItem
        {
            Header = CreateNodeHeader(fileName, isDirectory: false),
            Tag = filePath,
        };

        // Insert in sorted position among file children (after all directories)
        int insertIdx = parentNode.Items.Count;
        for (int i = 0; i < parentNode.Items.Count; i++)
        {
            if (parentNode.Items[i] is TreeViewItem child && child.Tag is string childPath)
            {
                if (Directory.Exists(childPath)) continue; // skip dirs
                if (string.Compare(Path.GetFileName(childPath), fileName, StringComparison.OrdinalIgnoreCase) > 0)
                {
                    insertIdx = i;
                    break;
                }
            }
        }
        parentNode.Items.Insert(insertIdx, newNode);
        parentNode.IsExpanded = true;
        newNode.IsSelected = true;
        newNode.BringIntoView();
    }

    /// <summary>Recursively find a TreeViewItem by its Tag path.</summary>
    private static TreeViewItem? FindNodeByPath(ItemCollection items, string path)
    {
        foreach (var item in items)
        {
            if (item is TreeViewItem tvi)
            {
                if (tvi.Tag is string tag && string.Equals(tag, path, StringComparison.OrdinalIgnoreCase))
                    return tvi;
                var found = FindNodeByPath(tvi.Items, path);
                if (found != null) return found;
            }
        }
        return null;
    }

    /// <summary>Returns directory path: if selected is a file, returns its parent.</summary>
    private string? GetSelectedDirPath()
    {
        var path = GetSelectedPath();
        if (path == null) return null;
        return Directory.Exists(path) ? path : Path.GetDirectoryName(path);
    }

    // ── Events ──

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshTree();

    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem item && item.Tag is string path && File.Exists(path))
        {
            FileSelected?.Invoke(path);
        }
    }

    private string? GetSelectedPath()
    {
        return tvFiles.SelectedItem is TreeViewItem item && item.Tag is string path ? path : null;
    }

    private void OnOpenInExplorer(object sender, RoutedEventArgs e)
    {
        var path = GetSelectedPath();
        if (path == null) return;

        if (File.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
    }

    private void OnCopyAbsolutePath(object sender, RoutedEventArgs e)
    {
        var path = GetSelectedPath();
        if (path == null) return;
        CopyToClipboard(path, "AbsPath");
    }

    private void OnCopyRelativePath(object sender, RoutedEventArgs e)
    {
        var path = GetSelectedPath();
        if (path == null) return;
        var relative = Path.GetRelativePath(_rootPath, path);
        CopyToClipboard(relative, "RelPath");
    }

    private void OnCopyName(object sender, RoutedEventArgs e)
    {
        var path = GetSelectedPath();
        if (path == null) return;
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) name = Path.GetDirectoryName(path) ?? path;
        CopyToClipboard(name, "FileName");
    }

    private void CopyToClipboard(string content, string source)
    {
        Clipboard.SetText(content);
        ClipboardCopied?.Invoke(content, source);
    }
}
