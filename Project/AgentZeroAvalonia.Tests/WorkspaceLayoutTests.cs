using Agent.Common.Services;
using AgentZeroAvalonia.Layout;
using Xunit;

namespace AgentZeroAvalonia.Tests;

public class WorkspaceLayoutTests
{
    private static (WorkspaceLayout<string> Layout, List<string> Tabs) Three()
    {
        var tabs = new List<string> { "a", "b", "c" };
        var layout = new WorkspaceLayout<string>();
        foreach (var t in tabs) layout.AddTab(t);
        return (layout, tabs);
    }

    [Fact]
    public void Unsplit_workspace_stores_nothing()
    {
        var (layout, tabs) = Three();
        Assert.Equal(1, layout.PaneCount);
        Assert.Null(layout.ToJson(tabs));
        Assert.Equal("c", layout.FirstPane.ActiveTab);
    }

    [Fact]
    public void Split_moves_the_tab_into_a_fresh_pane_after_its_own()
    {
        var (layout, tabs) = Three();
        var fresh = layout.SplitOut("b", vertical: false);
        Assert.NotNull(fresh);
        Assert.Equal(2, layout.PaneCount);
        Assert.Equal(new[] { "a", "c" }, layout.FirstPane.Tabs);
        Assert.Equal(new[] { "b" }, fresh!.Tabs);
        Assert.Equal("b", fresh.ActiveTab);
        Assert.Equal("{\"Children\":[{\"Tabs\":[0,2],\"Vertical\":false},{\"Tabs\":[1],\"Vertical\":false}],\"Vertical\":false}", layout.ToJson(tabs));
    }

    [Fact]
    public void A_lone_tab_cannot_split_out()
    {
        var layout = new WorkspaceLayout<string>();
        layout.AddTab("only");
        Assert.Null(layout.SplitOut("only", false));
        Assert.Equal(1, layout.PaneCount);
    }

    [Fact]
    public void Same_orientation_absorbs_and_cross_orientation_nests()
    {
        var tabs = new List<string> { "a", "b", "c", "d" };
        var layout = new WorkspaceLayout<string>();
        foreach (var t in tabs) layout.AddTab(t);

        layout.SplitOut("b", vertical: false);          // [a c d] | [b]
        layout.SplitOut("c", vertical: false);          // [a d] | [c] | [b]  — absorbed into the same row
        Assert.Equal(3, layout.PaneCount);
        Assert.IsType<SplitNode<string>>(layout.Root);
        Assert.Equal(3, ((SplitNode<string>)layout.Root).Children.Count);

        layout.SplitOut("d", vertical: true);           // [[a] / [d]] | [c] | [b] — nested column
        Assert.Equal(4, layout.PaneCount);
        var root = (SplitNode<string>)layout.Root;
        Assert.Equal(3, root.Children.Count);
        var nested = Assert.IsType<SplitNode<string>>(root.Children[0]);
        Assert.True(nested.Vertical);
        Assert.Equal("{\"Children\":[{\"Children\":[{\"Tabs\":[0],\"Vertical\":false},{\"Tabs\":[3],\"Vertical\":false}],\"Vertical\":true},{\"Tabs\":[2],\"Vertical\":false},{\"Tabs\":[1],\"Vertical\":false}],\"Vertical\":false}",
            layout.ToJson(tabs));
    }

    [Fact]
    public void Removing_the_last_tab_of_a_pane_collapses_the_split()
    {
        var (layout, tabs) = Three();
        var fresh = layout.SplitOut("b", false)!;
        layout.RemoveTab("b");
        Assert.Equal(1, layout.PaneCount);
        Assert.IsType<PaneNode<string>>(layout.Root);
        Assert.Null(layout.Root.Parent);
        Assert.Null(layout.ToJson(tabs));
        Assert.DoesNotContain(fresh, layout.Panes);
    }

    [Fact]
    public void Move_tab_empties_and_collapses_the_source()
    {
        var (layout, _) = Three();
        var fresh = layout.SplitOut("b", false)!;
        layout.MoveTab("b", layout.FirstPane);
        Assert.Equal(1, layout.PaneCount);
        Assert.Equal(new[] { "a", "c", "b" }, layout.FirstPane.Tabs);
        Assert.Equal("b", layout.FirstPane.ActiveTab);
        Assert.DoesNotContain(fresh, layout.Panes);
    }

    [Fact]
    public void Stored_layout_round_trips_and_unknown_tabs_land_in_the_first_pane()
    {
        var tabs = new List<string> { "a", "b", "c", "d" };
        // What the WPF host writes for  [a] | [[b] / [c]]  with d opened later.
        var stored = DockPaneLayout.ToJson(DockPaneNode.Group(false,
            DockPaneNode.Pane(0),
            DockPaneNode.Group(true, DockPaneNode.Pane(1), DockPaneNode.Pane(2))));
        // Panes carry "Vertical":false too: DockPaneNode.Vertical is a plain bool, and this is the byte-for-byte WPF row.
        Assert.Equal("{\"Children\":[{\"Tabs\":[0],\"Vertical\":false},{\"Children\":[{\"Tabs\":[1],\"Vertical\":false},{\"Tabs\":[2],\"Vertical\":false}],\"Vertical\":true}],\"Vertical\":false}", stored);

        var layout = WorkspaceLayout<string>.FromDock(DockPaneLayout.FromJson(stored), tabs);
        Assert.Equal(3, layout.PaneCount);
        Assert.Equal(new[] { "a", "d" }, layout.FirstPane.Tabs);     // d: not in the stored layout
        Assert.Equal(stored.Replace("[0]", "[0,3]"), layout.ToJson(tabs));

        // A tab that no longer exists is dropped and the empty pane pruned.
        var fewer = new List<string> { "a", "b" };
        var pruned = WorkspaceLayout<string>.FromDock(DockPaneLayout.FromJson(stored), fewer);
        Assert.Equal(2, pruned.PaneCount);
        Assert.Equal("{\"Children\":[{\"Tabs\":[0],\"Vertical\":false},{\"Tabs\":[1],\"Vertical\":false}],\"Vertical\":false}", pruned.ToJson(fewer));
    }

    [Fact]
    public void Corrupt_or_empty_layout_json_falls_back_to_one_pane()
    {
        var tabs = new List<string> { "a", "b" };
        Assert.Equal(1, WorkspaceLayout<string>.FromDock(DockPaneLayout.FromJson("not json"), tabs).PaneCount);
        Assert.Equal(1, WorkspaceLayout<string>.FromDock(null, tabs).PaneCount);
        Assert.Equal(new[] { "a", "b" }, WorkspaceLayout<string>.FromDock(null, tabs).FirstPane.Tabs);
    }

    [Fact]
    public void Neighbour_picks_the_pane_across_the_nearest_edge()
    {
        var tabs = new List<string> { "a", "b", "c" };
        var layout = new WorkspaceLayout<string>();
        foreach (var t in tabs) layout.AddTab(t);
        var right = layout.SplitOut("b", false)!;      // [a c] | [b]
        var below = layout.SplitOut("c", true)!;       // [[a] / [c]] | [b]
        var left = layout.FirstPane;

        var rects = new Dictionary<PaneNode<string>, (double, double, double, double)>
        {
            [left] = (0, 0, 100, 50),
            [below] = (0, 50, 100, 50),
            [right] = (100, 0, 100, 100),
        };
        Assert.Same(right, WorkspaceLayout<string>.Neighbour(left, FocusDirection.Right, rects));
        Assert.Same(below, WorkspaceLayout<string>.Neighbour(left, FocusDirection.Down, rects));
        Assert.Same(left, WorkspaceLayout<string>.Neighbour(below, FocusDirection.Up, rects));
        Assert.Null(WorkspaceLayout<string>.Neighbour(left, FocusDirection.Left, rects));
        // From the tall right pane, left prefers the top-left (equal gap, first best overlap wins ties by order).
        Assert.NotNull(WorkspaceLayout<string>.Neighbour(right, FocusDirection.Left, rects));
    }
}
