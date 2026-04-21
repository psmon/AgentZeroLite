using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentZeroWpf.Services;
using Docnet.Core;
using Docnet.Core.Models;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Win32;
using System.Windows.Documents;

namespace AgentZeroWpf.UI.Components;

public partial class DocumentViewerPanel : UserControl
{
    private string? _currentFile;
    private bool _isEditMode;
    private bool _isDirty;
    private MermaidRenderer? _mermaid;

    // Browser detection (cached)
    private static readonly Lazy<string?> _edgePath = new(DetectEdge);
    private static readonly Lazy<string?> _chromePath = new(DetectChrome);

    /// <summary>Fires when a code block copy happens (content, "CodeCopy").</summary>
    public event Action<string, string>? ClipboardCopied;

    public DocumentViewerPanel()
    {
        InitializeComponent();

        // AvalonEdit dark theme
        ApplyEditorTheme(txtEditor);
        ApplyEditorTheme(codeViewer);
    }

    private static void ApplyEditorTheme(ICSharpCode.AvalonEdit.TextEditor editor, bool isEditMode = false)
    {
        editor.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E1E")!);
        editor.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isEditMode ? "#FFFFFF" : "#D4D4D4")!);
        editor.LineNumbersForeground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#858585")!);
    }

    public async Task LoadFileAsync(string filePath)
    {
        if (_isDirty && _currentFile != null)
        {
            var r = MessageBox.Show("Save changes?", "Unsaved Changes",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes) SaveCurrentFile();
        }

        _currentFile = filePath;
        _isDirty = false;
        _isEditMode = false;

        var name = Path.GetFileName(filePath);
        txtFileName.Text = name;

        HideAllViews();
        HideBrowserButtons();

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var category = CategorizeFile(ext);
        var isHtml = ext is ".html" or ".htm";

        if (isHtml) ShowBrowserButtons();

        try
        {
            switch (category)
            {
                case FileCategory.Markdown:
                    await ShowMarkdownView(filePath);
                    btnEdit.Visibility = Visibility.Visible;
                    btnView.Visibility = Visibility.Visible;
                    HighlightViewButton();
                    break;

                case FileCategory.Code:
                    ShowCodeView(filePath, ext);
                    btnEdit.Visibility = Visibility.Collapsed;
                    btnView.Visibility = Visibility.Collapsed;
                    break;

                case FileCategory.Text:
                    ShowCodeView(filePath, ext);
                    btnEdit.Visibility = Visibility.Collapsed;
                    btnView.Visibility = Visibility.Collapsed;
                    break;

                case FileCategory.Image:
                    ShowImageView(filePath);
                    btnEdit.Visibility = Visibility.Collapsed;
                    btnView.Visibility = Visibility.Collapsed;
                    break;

                case FileCategory.Pdf:
                    ShowPdfView(filePath);
                    btnEdit.Visibility = Visibility.Collapsed;
                    btnView.Visibility = Visibility.Collapsed;
                    break;

                default:
                    txtUnsupported.Visibility = Visibility.Visible;
                    btnEdit.Visibility = Visibility.Collapsed;
                    btnView.Visibility = Visibility.Collapsed;
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Failed to load file: {filePath}", ex);
            txtUnsupported.Text = $"Failed to load: {ex.Message}";
            txtUnsupported.Visibility = Visibility.Visible;
        }
    }

    // ── Markdown ──

    private async Task ShowMarkdownView(string filePath)
    {
        var text = await File.ReadAllTextAsync(filePath);
        var hasMermaid = text.Contains("```mermaid", StringComparison.OrdinalIgnoreCase);

        try
        {
            mdViewer.MarkdownText = text;
            mdViewer.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Markdown render failed, falling back to raw view: {filePath}", ex);
            ShowCodeView(filePath, ".md");
            return;
        }

        if (hasMermaid)
        {
            try
            {
                await RenderMermaidBlocksAsync(text);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Mermaid rendering failed", ex);
            }
        }
    }

    private async Task RenderMermaidBlocksAsync(string markdownText)
    {
        _mermaid ??= new MermaidRenderer();

        try
        {
            await _mermaid.InitAsync();
        }
        catch
        {
            // WebView2 not available — mermaid stays as code blocks
            return;
        }

        // Extract mermaid blocks
        var lines = markdownText.Split('\n');
        bool inMermaid = false;
        var mermaidCode = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```mermaid", StringComparison.OrdinalIgnoreCase))
            {
                inMermaid = true;
                mermaidCode.Clear();
                continue;
            }
            if (inMermaid && line.TrimStart().StartsWith("```"))
            {
                inMermaid = false;
                var svg = await _mermaid.RenderAsync(mermaidCode.ToString());
                if (svg != null)
                {
                    // For now, mermaid diagrams rendered inline aren't easily replaceable
                    // in FlowDocument. Show first mermaid diagram in the dedicated SVG viewer.
                    ShowMermaidSvg(svg);
                }
                continue;
            }
            if (inMermaid)
            {
                mermaidCode.AppendLine(line);
            }
        }
    }

    private void ShowMermaidSvg(string svgContent)
    {
        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"agz_mermaid_{Guid.NewGuid():N}.svg");
            File.WriteAllText(tempPath, svgContent);
            svgMermaid.Source = new Uri(tempPath);
            svMermaid.Visibility = Visibility.Visible;
        }
        catch { /* SVG render failed, ignore */ }
    }

    // ── Code View ──

    private void ShowCodeView(string filePath, string ext)
    {
        var text = File.ReadAllText(filePath);
        var langName = GetHighlightingName(ext);
        var highlighting = langName != null
            ? HighlightingManager.Instance.GetDefinition(langName)
            : null;

        codeViewer.Text = text;
        codeViewer.SyntaxHighlighting = highlighting;
        codeViewer.Visibility = Visibility.Visible;

        // Add copy-all context menu
        codeViewer.ContextMenu ??= new ContextMenu();
        codeViewer.ContextMenu.Items.Clear();
        var copyItem = new MenuItem { Header = "Copy All" };
        copyItem.Click += (_, _) =>
        {
            Clipboard.SetText(text);
            ClipboardCopied?.Invoke(text, "CodeCopy");
        };
        codeViewer.ContextMenu.Items.Add(copyItem);

        if (codeViewer.TextArea.Selection.Length > 0)
        {
            var copySelItem = new MenuItem { Header = "Copy Selection" };
            copySelItem.Click += (_, _) =>
            {
                var sel = codeViewer.TextArea.Selection.GetText();
                Clipboard.SetText(sel);
                ClipboardCopied?.Invoke(sel, "CodeCopy");
            };
            codeViewer.ContextMenu.Items.Add(copySelItem);
        }
    }

    // ── Image View ──

    private void ShowImageView(string filePath)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(filePath);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            imgView.Source = bmp;
            svImage.Visibility = Visibility.Visible;
        }
        catch
        {
            txtUnsupported.Text = "Failed to load image";
            txtUnsupported.Visibility = Visibility.Visible;
        }
    }

    // ── PDF View ──

    private void ShowPdfView(string filePath)
    {
        pnlPdfPages.Children.Clear();
        try
        {
            using var docReader = DocLib.Instance.GetDocReader(filePath,
                new PageDimensions(1080, 1920));

            for (int i = 0; i < docReader.GetPageCount(); i++)
            {
                using var page = docReader.GetPageReader(i);
                var w = page.GetPageWidth();
                var h = page.GetPageHeight();
                var rawBytes = page.GetImage();

                var bmp = new WriteableBitmap(w, h, 96, 96,
                    PixelFormats.Bgra32, null);
                bmp.WritePixels(new Int32Rect(0, 0, w, h), rawBytes, w * 4, 0);

                var img = new System.Windows.Controls.Image
                {
                    Source = bmp,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    MaxWidth = 800,
                    Margin = new Thickness(0, 4, 0, 4),
                };
                pnlPdfPages.Children.Add(img);
            }

            svPdf.Visibility = Visibility.Visible;
        }
        catch
        {
            txtUnsupported.Text = "Failed to load PDF";
            txtUnsupported.Visibility = Visibility.Visible;
        }
    }

    // ── Edit/View Mode Toggle ──

    /// <summary>Load file and immediately enter edit mode (for newly created files).</summary>
    public async Task LoadFileInEditModeAsync(string filePath)
    {
        await LoadFileAsync(filePath);
        if (CategorizeFile(Path.GetExtension(filePath).ToLowerInvariant()) == FileCategory.Markdown)
            EnterEditMode();
    }

    private void OnEditClick(object sender, RoutedEventArgs e) => EnterEditMode();

    private void EnterEditMode()
    {
        if (_currentFile == null || CategorizeFile(Path.GetExtension(_currentFile).ToLowerInvariant()) != FileCategory.Markdown)
            return;

        _isEditMode = true;
        var text = File.ReadAllText(_currentFile);
        ApplyEditorTheme(txtEditor, isEditMode: true);
        txtEditor.Text = text;
        txtEditor.SyntaxHighlighting = null; // plain white text for readability

        HideAllViews();
        txtEditor.Visibility = Visibility.Visible;
        btnSave.Visibility = Visibility.Visible;
        HighlightEditButton();

        txtEditor.TextChanged += OnEditorTextChanged;
    }

    private async void OnViewClick(object sender, RoutedEventArgs e)
    {
        if (_currentFile == null) return;

        if (_isDirty)
        {
            var r = MessageBox.Show("Save changes before switching to view?", "Unsaved",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes) SaveCurrentFile();
        }

        txtEditor.TextChanged -= OnEditorTextChanged;
        _isEditMode = false;

        HideAllViews();
        btnSave.Visibility = Visibility.Collapsed;
        try
        {
            await ShowMarkdownView(_currentFile);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Failed to render markdown view: {_currentFile}", ex);
            ShowCodeView(_currentFile, ".md");
        }
        HighlightViewButton();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveCurrentFile();

    private void SaveCurrentFile()
    {
        if (_currentFile == null || !_isEditMode) return;
        File.WriteAllText(_currentFile, txtEditor.Text);
        _isDirty = false;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e) => _isDirty = true;

    // ── Helpers ──

    private static readonly SolidColorBrush BgDark = new((Color)ColorConverter.ConvertFromString("#1E1E1E")!);

    private void HideAllViews()
    {
        txtEmpty.Visibility = Visibility.Collapsed;
        mdViewer.Visibility = Visibility.Collapsed;
        txtEditor.Visibility = Visibility.Collapsed;
        codeViewer.Visibility = Visibility.Collapsed;
        svImage.Visibility = Visibility.Collapsed;
        svPdf.Visibility = Visibility.Collapsed;
        svMermaid.Visibility = Visibility.Collapsed;
        txtUnsupported.Visibility = Visibility.Collapsed;
        contentArea.Background = BgDark;
    }

    private void HighlightViewButton()
    {
        btnView.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3794FF")!);
        btnEdit.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#858585")!);
    }

    private void HighlightEditButton()
    {
        btnEdit.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3794FF")!);
        btnView.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#858585")!);
    }

    private enum FileCategory { Markdown, Code, Text, Image, Pdf, Binary }

    private static FileCategory CategorizeFile(string ext)
    {
        return ext switch
        {
            ".md" or ".mdx" or ".markdown" => FileCategory.Markdown,
            ".cs" or ".java" or ".py" or ".js" or ".ts" or ".tsx" or ".jsx"
                or ".cpp" or ".c" or ".h" or ".hpp" or ".go" or ".rs" or ".rb"
                or ".swift" or ".kt" or ".kts" or ".scala" or ".lua" or ".r"
                or ".ps1" or ".psm1" or ".sh" or ".bash" or ".bat" or ".cmd"
                or ".sql" or ".html" or ".htm" or ".css" or ".scss" or ".less"
                or ".vue" or ".svelte" or ".php" or ".pl" or ".dart"
                => FileCategory.Code,
            ".json" or ".xml" or ".yaml" or ".yml" or ".toml" or ".ini" or ".cfg"
                or ".conf" or ".properties" or ".env" or ".editorconfig"
                or ".gitignore" or ".gitattributes" or ".dockerfile" or ".dockerignore"
                or ".txt" or ".log" or ".csv" or ".tsv" or ".sln" or ".csproj" or ".fsproj"
                or ".xaml" or ".axaml" or ".razor"
                => FileCategory.Text,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico" or ".webp" or ".svg"
                => FileCategory.Image,
            ".pdf" => FileCategory.Pdf,
            _ => IsBinaryExtension(ext) ? FileCategory.Binary : FileCategory.Text,
        };
    }

    private static bool IsBinaryExtension(string ext)
    {
        return ext is ".exe" or ".dll" or ".pdb" or ".zip" or ".7z" or ".tar" or ".gz"
            or ".rar" or ".iso" or ".bin" or ".obj" or ".lib" or ".so" or ".dylib"
            or ".woff" or ".woff2" or ".ttf" or ".otf" or ".eot"
            or ".mp3" or ".mp4" or ".avi" or ".mkv" or ".mov" or ".wav" or ".flac"
            or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx";
    }

    private static string? GetHighlightingName(string ext)
    {
        return ext switch
        {
            ".cs" => "C#",
            ".java" => "Java",
            ".py" => "Python",
            ".js" or ".jsx" => "JavaScript",
            ".ts" or ".tsx" => "TypeScript",
            ".cpp" or ".c" or ".h" or ".hpp" => "C++",
            ".xml" or ".xaml" or ".axaml" or ".csproj" or ".fsproj" or ".sln" => "XML",
            ".html" or ".htm" or ".razor" or ".vue" or ".svelte" => "HTML",
            ".css" or ".scss" or ".less" => "CSS",
            ".json" => "JSON",
            ".sql" => "TSQL",
            ".ps1" or ".psm1" => "PowerShell",
            ".sh" or ".bash" => "Bash",
            ".bat" or ".cmd" => "BAT",
            ".md" or ".mdx" or ".markdown" => "MarkDown",
            ".php" => "PHP",
            ".yaml" or ".yml" => "YAML",
            _ => null,
        };
    }

    // ── Browser Open (HTML files) ──

    private void ShowBrowserButtons()
    {
        pnlBrowserButtons.Visibility = Visibility.Visible;
        btnOpenEdge.Visibility = _edgePath.Value != null ? Visibility.Visible : Visibility.Collapsed;
        btnOpenChrome.Visibility = _chromePath.Value != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideBrowserButtons()
    {
        pnlBrowserButtons.Visibility = Visibility.Collapsed;
    }

    private void OnOpenInEdge(object sender, RoutedEventArgs e)
    {
        if (_currentFile == null || _edgePath.Value == null) return;
        Process.Start(new ProcessStartInfo(_edgePath.Value, $"\"{_currentFile}\"") { UseShellExecute = false });
    }

    private void OnOpenInChrome(object sender, RoutedEventArgs e)
    {
        if (_currentFile == null || _chromePath.Value == null) return;
        Process.Start(new ProcessStartInfo(_chromePath.Value, $"\"{_currentFile}\"") { UseShellExecute = false });
    }

    private static string? DetectEdge()
    {
        // Edge is typically at this path on Windows
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Microsoft\Edge\Application\msedge.exe"),
        ];
        foreach (var p in candidates)
            if (File.Exists(p)) return p;

        // Fallback: registry
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
            var val = key?.GetValue(null)?.ToString();
            if (val != null && File.Exists(val)) return val;
        }
        catch { }

        return null;
    }

    private static string? DetectChrome()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\Application\chrome.exe"),
        ];
        foreach (var p in candidates)
            if (File.Exists(p)) return p;

        // Fallback: registry
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
            var val = key?.GetValue(null)?.ToString();
            if (val != null && File.Exists(val)) return val;
        }
        catch { }

        return null;
    }

    public void Cleanup()
    {
        _mermaid?.Dispose();
    }
}
