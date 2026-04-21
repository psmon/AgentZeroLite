using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentZeroWpf.Services;

namespace AgentZeroWpf.UI.APP;

public partial class NoteWindow : Window
{
    private readonly string _rootPath;
    private PencilRenderer? _penRenderer;
    private int _penCurrentIndex;

    public NoteWindow(string rootPath)
    {
        InitializeComponent();
        _rootPath = rootPath;

        txtPath.Text = rootPath;
        txtTitle.Text = $"NOTE — {Path.GetFileName(rootPath)}";
        Title = $"AgentZero — {Path.GetFileName(rootPath)}";

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// Hide title/status bars when the note is embedded inside another window.
    /// The host window provides its own close affordance.
    /// </summary>
    public void SetEmbeddedMode(bool embedded)
    {
        if (noteTitleBarRow is not null)
            noteTitleBarRow.Height = embedded ? new GridLength(0) : new GridLength(32);
        if (noteStatusBarRow is not null)
            noteStatusBarRow.Height = embedded ? new GridLength(0) : new GridLength(24);
    }

    /// <summary>Detach this window's content so it can be reparented elsewhere.</summary>
    public FrameworkElement? DetachContent()
    {
        var root = Content as FrameworkElement;
        Content = null;
        return root;
    }

    /// <summary>Re-attach previously-detached content as this window's child.</summary>
    public void AttachContent(FrameworkElement root)
    {
        Content = root;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeHelper.ApplyDarkTitleBar(this);
        InitializeCore();
    }

    /// <summary>
    /// Wire panel events and load the root into the file tree. Safe to call
    /// multiple times — guarded by <see cref="_initialized"/>. Call this
    /// explicitly when the note is embedded (no Window Show → no Loaded event).
    /// </summary>
    public void InitializeCore()
    {
        if (_initialized) return;
        _initialized = true;

        // Wire events
        fileTree.FileSelected += OnFileSelected;
        fileTree.FileCreated += OnFileCreated;
        fileTree.ClipboardCopied += OnClipboardCopied;
        docViewer.ClipboardCopied += OnClipboardCopied;
        clipHistory.FileEntryClicked += OnFileEntryClicked;
        clipHistory.ClipboardReused += OnClipboardReused;

        // Load file tree
        fileTree.LoadRoot(_rootPath);

        // Clipboard panel visible by default
        ShowClipboardPanel();
    }

    private bool _initialized;

    /// <summary>Normal file click → view mode.</summary>
    private async void OnFileSelected(string filePath)
    {
        txtStatus.Text = Path.GetFileName(filePath);

        if (filePath.EndsWith(".pen", StringComparison.OrdinalIgnoreCase))
        {
            LoadPenViewer(filePath);
            return;
        }

        ShowDocViewer();
        await docViewer.LoadFileAsync(filePath);
    }

    /// <summary>Show doc viewer, hide pen viewer.</summary>
    private void ShowDocViewer()
    {
        penViewerHost.Visibility = Visibility.Collapsed;
        docViewer.Visibility = Visibility.Visible;
        _penRenderer = null;
        pnlPenFrame.Child = null;
        pnlPenFrameButtons.Children.Clear();
    }

    /// <summary>Load .pen file into inline pen viewer panel (read-only).</summary>
    private void LoadPenViewer(string penPath)
    {
        var renderer = PencilRenderer.TryLoad(penPath);
        if (renderer is null || renderer.Frames.Count == 0)
        {
            txtStatus.Text = $"Failed to load .pen: {Path.GetFileName(penPath)}";
            return;
        }

        _penRenderer = renderer;
        _penCurrentIndex = 0;

        // Swap viewer visibility
        docViewer.Visibility = Visibility.Collapsed;
        penViewerHost.Visibility = Visibility.Visible;

        // Build frame buttons
        pnlPenFrameButtons.Children.Clear();
        for (int i = 0; i < renderer.Frames.Count; i++)
        {
            var idx = i;
            var frame = renderer.Frames[i];
            var btn = new Button
            {
                Content = frame.Name,
                Tag = idx,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 3, 3),
                Cursor = Cursors.Hand,
            };
            btn.Click += (_, _) => ShowPenFrame(idx);
            pnlPenFrameButtons.Children.Add(btn);
        }

        // Defer initial render until layout pass so svPenFrame.ActualWidth is valid
        Dispatcher.BeginInvoke(new Action(() => ShowPenFrame(0)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ShowPenFrame(int index)
    {
        if (_penRenderer is null || index < 0 || index >= _penRenderer.Frames.Count) return;

        _penCurrentIndex = index;
        var frame = _penRenderer.Frames[index];
        var available = svPenFrame.ActualWidth;
        var renderWidth = Math.Max(400, available > 40 ? available - 32 : 800);
        pnlPenFrame.Child = _penRenderer.RenderFrame(frame, renderWidth);
        txtPenFrameInfo.Text = $"{index + 1} / {_penRenderer.Frames.Count}  —  {frame.Name}  ({frame.Width}×{frame.Height})";

        foreach (var child in pnlPenFrameButtons.Children)
        {
            if (child is Button b && b.Tag is int idx)
            {
                var active = idx == index;
                b.Background = active
                    ? new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x78))
                    : Brushes.Transparent;
                b.Foreground = active
                    ? new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF))
                    : new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
            }
        }
    }

    private void OnPenFramePrev(object sender, RoutedEventArgs e)
    {
        if (_penRenderer is null) return;
        var idx = (_penCurrentIndex - 1 + _penRenderer.Frames.Count) % _penRenderer.Frames.Count;
        ShowPenFrame(idx);
    }

    private void OnPenFrameNext(object sender, RoutedEventArgs e)
    {
        if (_penRenderer is null) return;
        var idx = (_penCurrentIndex + 1) % _penRenderer.Frames.Count;
        ShowPenFrame(idx);
    }

    private void OnPenFrameViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_penRenderer is null) return;
        ShowPenFrame(_penCurrentIndex);
    }

    /// <summary>New file created → edit mode + add to history.</summary>
    private async void OnFileCreated(string filePath)
    {
        txtStatus.Text = $"Created: {Path.GetFileName(filePath)}";
        clipHistory.AddFileCreatedEntry(filePath);
        ShowDocViewer();
        await docViewer.LoadFileInEditModeAsync(filePath);
    }

    /// <summary>File entry clicked in history → navigate tree + open file in view mode.</summary>
    private async void OnFileEntryClicked(string filePath)
    {
        if (!File.Exists(filePath)) return;
        txtStatus.Text = Path.GetFileName(filePath);

        // Expand tree to file and select it
        fileTree.SelectFile(filePath);

        if (filePath.EndsWith(".pen", StringComparison.OrdinalIgnoreCase))
        {
            LoadPenViewer(filePath);
            return;
        }

        ShowDocViewer();
        await docViewer.LoadFileAsync(filePath);
    }

    private void OnClipboardReused(string content)
    {
        var preview = content.Length > 40 ? content[..40] + "…" : content;
        preview = preview.Replace("\r", "").Replace("\n", " ");
        toast.Show($"Copied: {preview}");
    }

    private void OnClipboardCopied(string content, string source)
    {
        clipHistory.AddEntry(content, source);

        // Toast notification
        var preview = content.Length > 40 ? content[..40] + "…" : content;
        preview = preview.Replace("\r", "").Replace("\n", " ");
        toast.Show($"Copied: {preview}");
    }

    // ── Clipboard Panel Toggle ──

    private void OnToggleClipboard(object sender, RoutedEventArgs e)
    {
        if (clipHistory.Visibility == Visibility.Visible)
            HideClipboardPanel();
        else
            ShowClipboardPanel();
    }

    private void ShowClipboardPanel()
    {
        colClipboard.Width = new GridLength(260);
        clipHistory.Visibility = Visibility.Visible;
        splClipboard.Visibility = Visibility.Visible;
        btnClipToggle.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
    }

    private void HideClipboardPanel()
    {
        colClipboard.Width = new GridLength(0);
        clipHistory.Visibility = Visibility.Collapsed;
        splClipboard.Visibility = Visibility.Collapsed;
        btnClipToggle.Foreground = (System.Windows.Media.Brush)FindResource("TextDim");
    }

    // ── Title Bar ──

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnWindowStateChanged(object? sender, EventArgs e)
        => MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

    private void OnClosed(object? sender, EventArgs e)
    {
        docViewer.Cleanup();
    }
}
