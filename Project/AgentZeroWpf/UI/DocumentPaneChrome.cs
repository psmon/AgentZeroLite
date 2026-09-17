using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AvalonDock.Layout;

namespace AgentZeroWpf.UI;

/// <summary>
/// Repurposes the small ▽ button AvalonDock puts at the right edge of every
/// document pane header (<c>MenuDropDownButton</c>).
///
/// <para>Out of the box it drops down a list of the tabs in that pane and
/// switches to the one you pick — a second way to do what clicking the tab
/// already does, drawn from the stock template so it ignores the app's theme and
/// is barely visible against it. Detaching a tab, meanwhile, had no button at
/// all: you had to drag the tab header out of the window and hope the drag was
/// read as a float rather than a re-order.</para>
///
/// <para>So the button keeps its place and loses its menu: one click detaches the
/// pane's current document into its own window. This is done by walking the
/// visual tree rather than by retemplating <c>LayoutDocumentPaneControl</c>,
/// because a retemplate means forking the whole VS2013 pane template — every
/// tab strip, drop target and overflow behaviour in it — to change one button.</para>
/// </summary>
internal static class DocumentPaneChrome
{
    /// <summary>The name AvalonDock's pane template gives the drop-down.</summary>
    private const string ButtonName = "MenuDropDownButton";

    /// <summary>
    /// Marks a button we have already dealt with. Panes come and go as the user
    /// splits and closes tabs, so this runs repeatedly over a tree that is mostly
    /// unchanged; without the flag each pass would stack another Click handler.
    /// </summary>
    private static readonly DependencyProperty PatchedProperty =
        DependencyProperty.RegisterAttached(
            "Patched", typeof(bool), typeof(DocumentPaneChrome), new PropertyMetadata(false));

    /// <summary>
    /// Find every pane drop-down under <paramref name="root"/> and turn it into a
    /// detach button.
    /// </summary>
    /// <param name="detach">
    /// Given the document the pane is currently showing. Not called when the pane
    /// is empty or already floating.
    /// </param>
    /// <returns>How many buttons were converted by this pass.</returns>
    public static int ApplyDetachButton(DependencyObject? root, Action<LayoutDocument> detach)
    {
        if (root is null) return 0;

        var converted = 0;
        foreach (var btn in Descendants<ToggleButton>(root))
        {
            if (btn.Name != ButtonName) continue;
            if ((bool)btn.GetValue(PatchedProperty)) continue;
            btn.SetValue(PatchedProperty, true);

            // The tab-switch menu. AvalonDock's DropDownButton opens whatever is in
            // this property on click, so clearing it is what removes the feature —
            // the Click below then has the button to itself.
            if (btn is AvalonDock.Controls.DropDownButton dd)
                dd.DropDownContextMenu = null;

            btn.ToolTip = "Detach this tab into its own window";
            btn.Content = new TextBlock
            {
                Text = "⤢",
                FontSize = 11,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Foreground = btn.TryFindResource("TextPrimary") as System.Windows.Media.Brush
                             ?? new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            };

            btn.Click += (s, _) =>
            {
                // It is a ToggleButton; without this it latches down after the click.
                if (s is ToggleButton t) t.IsChecked = false;
                if (DocumentOf(s as DependencyObject) is { } doc && !doc.IsFloating)
                    detach(doc);
            };
            converted++;
        }
        return converted;
    }

    /// <summary>
    /// The document the pane holding this button is showing. Walks up to the pane
    /// control rather than reading the button's DataContext, which AvalonDock sets
    /// for the drop-down menu and not for the button itself.
    /// </summary>
    private static LayoutDocument? DocumentOf(DependencyObject? from)
    {
        for (var d = from; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is AvalonDock.Controls.LayoutDocumentPaneControl pane)
                return (pane.Model as LayoutDocumentPane)?.SelectedContent as LayoutDocument;
        }
        return null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}
