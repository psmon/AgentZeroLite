using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.EntityFrameworkCore;

namespace AgentZeroWpf.UI.Components;

public partial class ClipboardHistoryPanel : UserControl
{
    private const int MaxEntries = 10;
    private readonly ObservableCollection<ClipboardItemVm> _allItems = [];
    private readonly ObservableCollection<ClipboardItemVm> _filteredItems = [];
    private string _activeFilter = "All"; // "All", "File", "Clipboard"
    private bool _updatingFilter;

    /// <summary>Fires when a FileCreated entry is clicked — navigate to that file in view mode.</summary>
    public event Action<string>? FileEntryClicked;

    /// <summary>Fires when a clipboard entry is reused (content copied). For toast notification.</summary>
    public event Action<string>? ClipboardReused;

    public ClipboardHistoryPanel()
    {
        InitializeComponent();
        lbHistory.ItemsSource = _filteredItems;
        Loaded += (_, _) => LoadFromDb();
    }

    /// <summary>Add a clipboard copy entry. Saves to SQLite.</summary>
    public void AddEntry(string content, string source)
    {
        if (string.IsNullOrEmpty(content)) return;
        SaveAndInsert(content, source);
    }

    /// <summary>Add a file-created entry. Saves to SQLite.</summary>
    public void AddFileCreatedEntry(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        SaveAndInsert(filePath, "NewFile");
    }

    private void SaveAndInsert(string content, string source)
    {
        var entry = new ClipboardEntry
        {
            Content = content,
            Source = source,
            CopiedAt = DateTime.UtcNow,
        };

        using var db = new AppDbContext();
        db.ClipboardEntries.Add(entry);
        db.SaveChanges();

        // Trim old entries beyond max
        var excess = db.ClipboardEntries
            .OrderByDescending(e => e.CopiedAt)
            .Skip(MaxEntries)
            .ToList();
        if (excess.Count > 0)
        {
            db.ClipboardEntries.RemoveRange(excess);
            db.SaveChanges();
        }

        var vm = ToVm(entry);
        _allItems.Insert(0, vm);
        while (_allItems.Count > MaxEntries) _allItems.RemoveAt(_allItems.Count - 1);

        ApplyFilter();
    }

    private void LoadFromDb()
    {
        _allItems.Clear();
        using var db = new AppDbContext();
        var entries = db.ClipboardEntries
            .OrderByDescending(e => e.CopiedAt)
            .Take(MaxEntries)
            .ToList();

        foreach (var e in entries)
            _allItems.Add(ToVm(e));

        ApplyFilter();
    }

    // ── Filter ──

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingFilter) return;
        if (filterAll == null || filterFile == null || filterClip == null) return;
        if (sender is not ToggleButton clicked) return;

        _updatingFilter = true;
        try
        {
            // Radio-button behavior: uncheck others, keep clicked one checked
            filterAll.IsChecked = clicked == filterAll;
            filterFile.IsChecked = clicked == filterFile;
            filterClip.IsChecked = clicked == filterClip;

            _activeFilter = clicked == filterFile ? "File"
                : clicked == filterClip ? "Clipboard"
                : "All";

            ApplyFilter();
        }
        finally
        {
            _updatingFilter = false;
        }
    }

    private void ApplyFilter()
    {
        _filteredItems.Clear();
        foreach (var item in _allItems)
        {
            if (_activeFilter == "All"
                || (_activeFilter == "File" && item.IsFileEntry)
                || (_activeFilter == "Clipboard" && !item.IsFileEntry))
            {
                _filteredItems.Add(item);
            }
        }
    }

    // ── Item Click ──

    private void OnItemSelected(object sender, SelectionChangedEventArgs e)
    {
        if (lbHistory.SelectedItem is not ClipboardItemVm vm) return;

        if (vm.IsFileEntry)
        {
            // Navigate to file in view mode
            FileEntryClicked?.Invoke(vm.FullContent);
        }
        else
        {
            // Reuse clipboard — do NOT add to history
            Clipboard.SetText(vm.FullContent);
            ClipboardReused?.Invoke(vm.FullContent);
        }

        lbHistory.SelectedIndex = -1; // deselect
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        using var db = new AppDbContext();
        db.ClipboardEntries.ExecuteDelete();
        _allItems.Clear();
        _filteredItems.Clear();
    }

    private static ClipboardItemVm ToVm(ClipboardEntry entry) => new()
    {
        FullContent = entry.Content,
        Preview = entry.Source == "NewFile"
            ? $"+ {System.IO.Path.GetFileName(entry.Content)}"
            : Truncate(entry.Content, 80),
        Source = entry.Source,
        TimeLabel = entry.CopiedAt.ToLocalTime().ToString("HH:mm:ss"),
        IsFileEntry = entry.Source == "NewFile",
    };

    private static string Truncate(string s, int max)
    {
        var clean = s.Replace("\r", "").Replace("\n", " ");
        return clean.Length <= max ? clean : clean[..(max - 1)] + "…";
    }
}

internal sealed class ClipboardItemVm
{
    public string FullContent { get; init; } = "";
    public string Preview { get; init; } = "";
    public string Source { get; init; } = "";
    public string TimeLabel { get; init; } = "";
    public bool IsFileEntry { get; init; }
}
