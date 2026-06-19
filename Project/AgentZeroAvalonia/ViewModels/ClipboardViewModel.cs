using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AgentZeroAvalonia.Models;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// 클립보드 히스토리. Avalonia는 클립보드 변경 이벤트가 없어 View가 주기적으로
/// 폴링하며 <see cref="Observe"/>로 텍스트를 밀어넣는다. 항목 클릭 시 다시 복사.
/// </summary>
public partial class ClipboardViewModel : ObservableObject
{
    private const int MaxEntries = 100;
    private string? _last;

    private readonly ObservableCollection<ClipboardEntry> _all = new();
    public ObservableCollection<ClipboardEntry> Filtered { get; } = new();

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private int _count;

    partial void OnFilterTextChanged(string value) => Rebuild();

    /// <summary>폴링된 클립보드 텍스트를 관찰. 직전 값과 다르면 히스토리에 추가.</summary>
    public void Observe(string? text)
    {
        if (string.IsNullOrEmpty(text) || text == _last) return;
        _last = text;
        _all.Insert(0, new ClipboardEntry { Text = text });
        while (_all.Count > MaxEntries) _all.RemoveAt(_all.Count - 1);
        Count = _all.Count;
        Rebuild();
    }

    private void Rebuild()
    {
        Filtered.Clear();
        var f = FilterText?.Trim() ?? "";
        foreach (var e in _all)
        {
            if (f.Length == 0 || e.Text.Contains(f, StringComparison.OrdinalIgnoreCase))
                Filtered.Add(e);
        }
    }

    [RelayCommand]
    private void Clear()
    {
        _all.Clear();
        Filtered.Clear();
        Count = 0;
        _last = null;
    }
}
