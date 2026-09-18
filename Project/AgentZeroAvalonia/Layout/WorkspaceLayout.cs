using Agent.Common.Services;

namespace AgentZeroAvalonia.Layout;

/// <summary>A node of the split tree: a pane of tabs, or a split holding nodes.</summary>
public abstract class LayoutNode<T> where T : class
{
    public SplitNode<T>? Parent { get; internal set; }
}

/// <summary>One pane: an ordered set of tabs, one of them showing.</summary>
public sealed class PaneNode<T> : LayoutNode<T> where T : class
{
    public List<T> Tabs { get; } = new();
    public T? ActiveTab { get; set; }

    /// <summary>Keep <see cref="ActiveTab"/> pointing at something the pane still holds.</summary>
    internal void FixActive()
    {
        if (ActiveTab is not null && Tabs.Contains(ActiveTab)) return;
        ActiveTab = Tabs.Count > 0 ? Tabs[0] : null;
    }
}

/// <summary>Children side by side (<see cref="Vertical"/> = false) or stacked top to bottom.</summary>
public sealed class SplitNode<T> : LayoutNode<T> where T : class
{
    public bool Vertical { get; set; }
    public List<LayoutNode<T>> Children { get; } = new();
}

public enum FocusDirection { Left, Right, Up, Down }

/// <summary>
/// The split tree of one workspace (M0036), in memory. Maps to and from the WPF host's
/// persisted <see cref="DockPaneNode"/> (tab indexes into the workspace's flat tab list),
/// and implements the WPF host's rules: a split moves the tab into a fresh sibling pane,
/// a same-orientation parent absorbs the new pane, an emptied pane collapses its parent,
/// and a layout with one pane is not worth storing (<see cref="DockPaneLayout.ToJson"/>).
/// Pure model — no UI types — so the tests drive it with strings.
/// </summary>
public sealed class WorkspaceLayout<T> where T : class
{
    public LayoutNode<T> Root { get; private set; }

    /// <summary>Raised after any structural change (split, move, remove, reset).</summary>
    public event Action? Changed;

    public WorkspaceLayout()
    {
        Root = new PaneNode<T>();
    }

    // ── queries ──

    public IEnumerable<PaneNode<T>> Panes => Walk(Root);

    public int PaneCount => Panes.Count();

    public PaneNode<T> FirstPane => Panes.First();

    public PaneNode<T>? PaneOf(T tab) => Panes.FirstOrDefault(p => p.Tabs.Contains(tab));

    private static IEnumerable<PaneNode<T>> Walk(LayoutNode<T> node)
    {
        if (node is PaneNode<T> pane)
        {
            yield return pane;
            yield break;
        }
        foreach (var child in ((SplitNode<T>)node).Children)
            foreach (var p in Walk(child)) yield return p;
    }

    // ── mutations ──

    /// <summary>Put a tab into <paramref name="pane"/> (or the first pane) and make it that pane's active tab.</summary>
    public PaneNode<T> AddTab(T tab, PaneNode<T>? pane = null)
    {
        pane ??= FirstPane;
        if (PaneOf(tab) is { } already) return already;
        pane.Tabs.Add(tab);
        pane.ActiveTab = tab;
        Changed?.Invoke();
        return pane;
    }

    /// <summary>Remove a tab; an emptied pane disappears (unless it is the only one).</summary>
    public void RemoveTab(T tab)
    {
        var pane = PaneOf(tab);
        if (pane is null) return;
        pane.Tabs.Remove(tab);
        pane.FixActive();
        if (pane.Tabs.Count == 0 && pane.Parent is not null) RemovePane(pane);
        Changed?.Invoke();
    }

    /// <summary>
    /// Split: move <paramref name="tab"/> out of its pane into a fresh pane placed after it,
    /// side by side (<paramref name="vertical"/> = false) or below. Returns the new pane, or
    /// null when the tab is alone in its pane — the WPF host skips that split too, as there
    /// would be nothing left behind; callers open a new terminal instead.
    /// </summary>
    public PaneNode<T>? SplitOut(T tab, bool vertical)
    {
        var pane = PaneOf(tab);
        if (pane is null || pane.Tabs.Count < 2) return null;
        pane.Tabs.Remove(tab);
        pane.FixActive();
        var fresh = new PaneNode<T>();
        fresh.Tabs.Add(tab);
        fresh.ActiveTab = tab;
        InsertAfter(pane, fresh, vertical);
        Changed?.Invoke();
        return fresh;
    }

    /// <summary>Split with an empty pane next to <paramref name="pane"/> (the caller fills it with a new tab).</summary>
    public PaneNode<T> SplitEmpty(PaneNode<T> pane, bool vertical)
    {
        var fresh = new PaneNode<T>();
        InsertAfter(pane, fresh, vertical);
        Changed?.Invoke();
        return fresh;
    }

    /// <summary>Move a tab into another pane (at the end), making it active there.</summary>
    public void MoveTab(T tab, PaneNode<T> target)
    {
        var source = PaneOf(tab);
        if (source is null || ReferenceEquals(source, target)) return;
        source.Tabs.Remove(tab);
        source.FixActive();
        target.Tabs.Add(tab);
        target.ActiveTab = tab;
        if (source.Tabs.Count == 0 && source.Parent is not null) RemovePane(source);
        Changed?.Invoke();
    }

    /// <summary>The pane after <paramref name="pane"/> in reading order, wrapping.</summary>
    public PaneNode<T> NextPane(PaneNode<T> pane)
    {
        var all = Panes.ToList();
        var i = all.IndexOf(pane);
        return all[(i + 1) % all.Count];
    }

    private void InsertAfter(PaneNode<T> pane, PaneNode<T> fresh, bool vertical)
    {
        var parent = pane.Parent;
        if (parent is null)
        {
            // The root was a lone pane: it becomes a split of the two.
            var split = new SplitNode<T> { Vertical = vertical };
            Attach(split, pane);
            Attach(split, fresh);
            Root = split;
            return;
        }
        if (parent.Vertical == vertical || parent.Children.Count == 1)
        {
            parent.Vertical = vertical;
            var idx = parent.Children.IndexOf(pane);
            fresh.Parent = parent;
            parent.Children.Insert(idx + 1, fresh);
            return;
        }
        // Cross-orientation: wrap the pane and the newcomer in a nested split.
        var wrapper = new SplitNode<T> { Vertical = vertical };
        var at = parent.Children.IndexOf(pane);
        parent.Children[at] = wrapper;
        wrapper.Parent = parent;
        Attach(wrapper, pane);
        Attach(wrapper, fresh);
    }

    private static void Attach(SplitNode<T> split, LayoutNode<T> child)
    {
        child.Parent = split;
        split.Children.Add(child);
    }

    private void RemovePane(PaneNode<T> pane)
    {
        var parent = pane.Parent!;
        parent.Children.Remove(pane);
        pane.Parent = null;
        Collapse(parent);
    }

    /// <summary>A split with one child is that child; with none it is gone.</summary>
    private void Collapse(SplitNode<T> split)
    {
        if (split.Children.Count > 1) return;
        var grand = split.Parent;
        var survivor = split.Children.Count == 1 ? split.Children[0] : null;
        if (grand is null)
        {
            Root = survivor ?? new PaneNode<T>();
            Root.Parent = null;
            return;
        }
        var at = grand.Children.IndexOf(split);
        if (survivor is null)
        {
            grand.Children.RemoveAt(at);
            Collapse(grand);
            return;
        }
        grand.Children[at] = survivor;
        survivor.Parent = grand;
        // A child split of the same orientation merges into its parent.
        if (survivor is SplitNode<T> s && s.Vertical == grand.Vertical)
        {
            grand.Children.RemoveAt(at);
            foreach (var c in s.Children) c.Parent = grand;
            grand.Children.InsertRange(at, s.Children);
        }
    }

    // ── persistence mapping (DockPaneNode: tab indexes into the flat list) ──

    /// <summary>Build from a stored layout; tabs it does not mention land in the first pane (Normalise).</summary>
    public static WorkspaceLayout<T> FromDock(DockPaneNode? stored, IReadOnlyList<T> tabs)
    {
        var layout = new WorkspaceLayout<T>();
        var placeable = Enumerable.Range(0, tabs.Count).ToList();
        var normalised = DockPaneLayout.Normalise(stored, placeable);
        if (normalised is null) return layout;
        layout.Root = Build(normalised, tabs, null);
        foreach (var p in layout.Panes) p.FixActive();
        return layout;
    }

    private static LayoutNode<T> Build(DockPaneNode node, IReadOnlyList<T> tabs, SplitNode<T>? parent)
    {
        if (node.IsPane)
        {
            var pane = new PaneNode<T> { Parent = parent };
            foreach (var i in node.Tabs!)
                if (i >= 0 && i < tabs.Count) pane.Tabs.Add(tabs[i]);
            return pane;
        }
        var split = new SplitNode<T> { Vertical = node.Vertical, Parent = parent };
        foreach (var child in node.Children ?? new())
            split.Children.Add(Build(child, tabs, split));
        return split;
    }

    /// <summary>The stored form: tab indexes into <paramref name="tabs"/>, exactly what the WPF host writes.</summary>
    public DockPaneNode? ToDock(IReadOnlyList<T> tabs)
    {
        var index = new Dictionary<T, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < tabs.Count; i++) index[tabs[i]] = i;
        return Capture(Root, index);
    }

    private static DockPaneNode? Capture(LayoutNode<T> node, IReadOnlyDictionary<T, int> index)
    {
        if (node is PaneNode<T> pane)
        {
            var ids = pane.Tabs.Where(index.ContainsKey).Select(t => index[t]).ToList();
            return ids.Count == 0 ? null : new DockPaneNode { Tabs = ids };
        }
        var split = (SplitNode<T>)node;
        var kids = split.Children.Select(c => Capture(c, index)).OfType<DockPaneNode>().ToList();
        return kids.Count switch
        {
            0 => null,
            1 => kids[0],
            _ => new DockPaneNode { Vertical = split.Vertical, Children = kids },
        };
    }

    /// <summary>The JSON the workspace row stores (null while unsplit).</summary>
    public string? ToJson(IReadOnlyList<T> tabs) => DockPaneLayout.ToJson(ToDock(tabs));

    // ── geometry helper for pane focus (rects are supplied by the view) ──

    /// <summary>The pane in <paramref name="dir"/> from <paramref name="from"/>, by rectangle: nearest edge, most overlap.</summary>
    public static PaneNode<T>? Neighbour(PaneNode<T> from, FocusDirection dir, IReadOnlyDictionary<PaneNode<T>, (double X, double Y, double W, double H)> rects)
    {
        if (!rects.TryGetValue(from, out var a)) return null;
        PaneNode<T>? best = null;
        var bestScore = double.MaxValue;
        foreach (var (pane, b) in rects)
        {
            if (ReferenceEquals(pane, from)) continue;
            double gap, overlap;
            switch (dir)
            {
                case FocusDirection.Left:
                    gap = a.X - (b.X + b.W);
                    overlap = Overlap(a.Y, a.H, b.Y, b.H);
                    break;
                case FocusDirection.Right:
                    gap = b.X - (a.X + a.W);
                    overlap = Overlap(a.Y, a.H, b.Y, b.H);
                    break;
                case FocusDirection.Up:
                    gap = a.Y - (b.Y + b.H);
                    overlap = Overlap(a.X, a.W, b.X, b.W);
                    break;
                default:
                    gap = b.Y - (a.Y + a.H);
                    overlap = Overlap(a.X, a.W, b.X, b.W);
                    break;
            }
            if (gap < -1 || overlap <= 0) continue;
            var score = gap * 1000 - overlap;
            if (score < bestScore)
            {
                bestScore = score;
                best = pane;
            }
        }
        return best;
    }

    private static double Overlap(double a, double aLen, double b, double bLen)
        => Math.Min(a + aLen, b + bLen) - Math.Max(a, b);
}
