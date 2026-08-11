using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Agent.Common.Module;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using Brush = System.Windows.Media.Brush;
using BrushConverter = System.Windows.Media.BrushConverter;

namespace AgentZeroWpf.UI.Components;

/// <summary>One selectable action in the command palette.</summary>
public sealed record PaletteItem(string Label, string Category, Action Invoke);

/// <summary>
/// A lightweight fuzzy command palette (Ctrl+J). A borderless popup with a
/// search box and a results list; typing fuzzy-filters over workspaces,
/// terminals and commands via the pure <see cref="FuzzyMatcher"/>. Enter (or
/// double-click) invokes the selection; Esc closes. Built programmatically to
/// stay self-contained.
/// </summary>
public sealed class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<PaletteItem> _all;
    private readonly TextBox _search;
    private readonly ListBox _list;

    public CommandPaletteWindow(Window owner, IReadOnlyList<PaletteItem> items)
    {
        _all = items;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var border = new Border
        {
            Background = (Brush)(new BrushConverter().ConvertFromString("#252526") ?? Brushes.Black),
            BorderBrush = (Brush)(new BrushConverter().ConvertFromString("#4ec9b0") ?? Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
        };
        var panel = new StackPanel();
        _search = new TextBox
        {
            FontSize = 15,
            FontFamily = new FontFamily("Consolas"),
            Background = (Brush)(new BrushConverter().ConvertFromString("#1e1e1e") ?? Brushes.Black),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6),
            CaretBrush = Brushes.White,
        };
        _list = new ListBox
        {
            MaxHeight = 360,
            Margin = new Thickness(0, 6, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Consolas"),
        };
        panel.Children.Add(_search);
        panel.Children.Add(_list);
        border.Child = panel;
        Content = border;

        _search.TextChanged += (_, _) => Refresh();
        _search.PreviewKeyDown += OnSearchKeyDown;
        _list.MouseDoubleClick += (_, _) => InvokeSelected();
        Loaded += (_, _) => { PositionOverOwner(); _search.Focus(); };
        Deactivated += (_, _) => Close();

        Refresh();
    }

    private void PositionOverOwner()
    {
        if (Owner is null) return;
        Left = Owner.Left + (Owner.ActualWidth - Width) / 2;
        Top = Owner.Top + 120;
    }

    private void Refresh()
    {
        var ranked = FuzzyMatcher.Rank(_search.Text, _all, i => i.Label + " " + i.Category);
        _list.Items.Clear();
        foreach (var item in ranked)
        {
            _list.Items.Add(new ListBoxItem
            {
                Content = $"{item.Label}    ·  {item.Category}",
                Tag = item,
                Foreground = Brushes.White,
                Padding = new Thickness(6, 3, 6, 3),
            });
        }
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Enter: InvokeSelected(); e.Handled = true; break;
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
        }
    }

    private void Move(int delta)
    {
        if (_list.Items.Count == 0) return;
        int next = _list.SelectedIndex + delta;
        _list.SelectedIndex = Math.Clamp(next, 0, _list.Items.Count - 1);
        _list.ScrollIntoView(_list.SelectedItem);
    }

    private void InvokeSelected()
    {
        if (_list.SelectedItem is ListBoxItem { Tag: PaletteItem item })
        {
            Close();
            try { item.Invoke(); } catch { /* invocation errors must not crash the palette */ }
        }
    }
}
