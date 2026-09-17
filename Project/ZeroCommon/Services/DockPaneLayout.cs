namespace Agent.Common.Services;

/// <summary>
/// One node of a remembered split: either a pane holding tabs, or a row/column
/// of further nodes.
/// </summary>
/// <remarks>
/// Tabs are held as indices into the workspace's tab list rather than by title,
/// because titles are neither unique nor stable — two "CMD" tabs in one
/// workspace is the normal case, and renaming one must not move it.
/// </remarks>
public sealed class DockPaneNode
{
    /// <summary>Tab indices in this pane, in tab order. Null for a group.</summary>
    public List<int>? Tabs { get; set; }

    /// <summary>Child nodes. Null for a pane.</summary>
    public List<DockPaneNode>? Children { get; set; }

    /// <summary>Stacked top-to-bottom rather than left-to-right. Groups only.</summary>
    public bool Vertical { get; set; }

    public bool IsPane => Tabs is not null;

    public static DockPaneNode Pane(params int[] tabs) => new() { Tabs = [.. tabs] };

    public static DockPaneNode Group(bool vertical, params DockPaneNode[] children) =>
        new() { Vertical = vertical, Children = [.. children] };
}

/// <summary>
/// Keeps a workspace's split arrangement usable after the tabs underneath it
/// have changed.
///
/// <para>A remembered layout goes stale the moment the workspace is edited
/// somewhere else: a tab closed while you were away leaves an index that no
/// longer resolves, a tab opened leaves a tab the layout has never heard of, and
/// a tab detached into its own window must not be dragged back into a pane just
/// because the workspace was revisited. Replaying a stale layout without fixing
/// it first either drops terminals off the screen or shows one twice.</para>
///
/// <para>So nothing is replayed directly. <see cref="Normalise"/> reconciles the
/// remembered shape with the tabs that actually exist and are actually
/// placeable, and the result is what gets rebuilt — with the guarantee the UI
/// needs: every placeable tab appears exactly once.</para>
/// </summary>
public static class DockPaneLayout
{
    /// <summary>
    /// Reconcile a remembered layout with the tabs there are now.
    /// </summary>
    /// <param name="node">The remembered shape, or null if there isn't one.</param>
    /// <param name="placeable">
    /// Tab indices that should end up in a pane. Excludes tabs that are floating
    /// in their own window — those already have somewhere to be.
    /// </param>
    /// <returns>
    /// A layout in which every index in <paramref name="placeable"/> appears
    /// exactly once and nothing else appears at all, or null when there is
    /// nothing to place.
    /// </returns>
    public static DockPaneNode? Normalise(DockPaneNode? node, IReadOnlyCollection<int> placeable)
    {
        if (placeable.Count == 0) return null;

        var wanted = new HashSet<int>(placeable);
        var seen = new HashSet<int>();
        var cleaned = Prune(node, wanted, seen);

        // Tabs the remembered layout knows nothing about — opened elsewhere, or
        // docked back from a floating window while this workspace was away.
        var missing = placeable.Where(i => !seen.Contains(i)).OrderBy(i => i).ToList();

        if (cleaned is null)
            return DockPaneNode.Pane([.. missing]);

        if (missing.Count > 0)
            FirstPane(cleaned).Tabs!.AddRange(missing);

        return cleaned;
    }

    /// <summary>
    /// Drop what no longer resolves and collapse what that leaves behind: an
    /// emptied pane, and a group with one child — which is not a split any more,
    /// it is just that child.
    /// </summary>
    private static DockPaneNode? Prune(DockPaneNode? node, HashSet<int> wanted, HashSet<int> seen)
    {
        if (node is null) return null;

        if (node.IsPane)
        {
            // `seen` also does the de-duplicating: an index already placed is not
            // placed again, so a layout that somehow names a tab twice resolves
            // to the first pane that claimed it.
            var tabs = node.Tabs!.Where(i => wanted.Contains(i) && seen.Add(i)).ToList();
            return tabs.Count == 0 ? null : new DockPaneNode { Tabs = tabs };
        }

        var kids = new List<DockPaneNode>();
        foreach (var child in node.Children ?? [])
            if (Prune(child, wanted, seen) is { } kept) kids.Add(kept);

        return kids.Count switch
        {
            0 => null,
            1 => kids[0],
            _ => new DockPaneNode { Vertical = node.Vertical, Children = kids },
        };
    }

    /// <summary>The pane a tab with no remembered home should join.</summary>
    private static DockPaneNode FirstPane(DockPaneNode node) =>
        node.IsPane ? node : FirstPane(node.Children![0]);

    /// <summary>
    /// Every tab index the layout places, in layout order. For assertions and
    /// for logging what was restored.
    /// </summary>
    public static IEnumerable<int> Tabs(DockPaneNode? node)
    {
        if (node is null) yield break;
        if (node.IsPane)
        {
            foreach (var i in node.Tabs!) yield return i;
            yield break;
        }
        foreach (var child in node.Children ?? [])
            foreach (var i in Tabs(child)) yield return i;
    }

    /// <summary>How many panes the layout has — 1 means it is not split.</summary>
    public static int PaneCount(DockPaneNode? node) =>
        node is null ? 0
        : node.IsPane ? 1
        : (node.Children ?? []).Sum(PaneCount);
}
