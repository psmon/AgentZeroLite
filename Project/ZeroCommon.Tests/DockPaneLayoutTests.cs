using Agent.Common.Services;

namespace ZeroCommon.Tests;

/// <summary>
/// The split a workspace remembers, replayed against the tabs it has when you
/// come back to it. The rule the UI depends on is the same in every case: each
/// placeable tab lands in exactly one pane. Break it and a terminal either
/// vanishes from the screen or is shown twice.
/// </summary>
public class DockPaneLayoutTests
{
    private static int[] All(int n) => Enumerable.Range(0, n).ToArray();

    // ── nothing remembered ──

    [Fact]
    public void NoLayout_PutsEveryTabInOnePane()
    {
        var r = DockPaneLayout.Normalise(null, All(3));

        Assert.NotNull(r);
        Assert.True(r!.IsPane);
        Assert.Equal([0, 1, 2], r.Tabs);
    }

    [Fact]
    public void NoTabs_IsNoLayout()
    {
        Assert.Null(DockPaneLayout.Normalise(DockPaneNode.Pane(0, 1), []));
        Assert.Null(DockPaneLayout.Normalise(null, []));
    }

    // ── the layout still fits ──

    [Fact]
    public void UnchangedWorkspace_KeepsTheSplitExactly()
    {
        var saved = DockPaneNode.Group(false, DockPaneNode.Pane(0), DockPaneNode.Pane(1));

        var r = DockPaneLayout.Normalise(saved, All(2));

        Assert.Equal(2, DockPaneLayout.PaneCount(r));
        Assert.Equal([0, 1], DockPaneLayout.Tabs(r));
        Assert.False(r!.Vertical);
    }

    [Fact]
    public void NestedSplit_Survives()
    {
        // left column | (right split top/bottom)
        var saved = DockPaneNode.Group(false,
            DockPaneNode.Pane(0),
            DockPaneNode.Group(true, DockPaneNode.Pane(1), DockPaneNode.Pane(2)));

        var r = DockPaneLayout.Normalise(saved, All(3));

        Assert.Equal(3, DockPaneLayout.PaneCount(r));
        Assert.Equal([0, 1, 2], DockPaneLayout.Tabs(r));
        Assert.True(r!.Children![1].Vertical);
    }

    // ── the workspace changed while we were away ──

    [Fact]
    public void ClosedTab_LeavesTheRestSplit()
    {
        var saved = DockPaneNode.Group(false,
            DockPaneNode.Pane(0, 1), DockPaneNode.Pane(2));

        var r = DockPaneLayout.Normalise(saved, [0, 1]);   // tab 2 is gone

        Assert.Equal([0, 1], DockPaneLayout.Tabs(r));
        Assert.Equal(1, DockPaneLayout.PaneCount(r));      // the split collapsed with it
        Assert.True(r!.IsPane);
    }

    [Fact]
    public void NewTab_JoinsTheFirstPane_AndIsNotLost()
    {
        var saved = DockPaneNode.Group(false, DockPaneNode.Pane(0), DockPaneNode.Pane(1));

        var r = DockPaneLayout.Normalise(saved, All(3));   // tab 2 is new

        Assert.Equal(2, DockPaneLayout.PaneCount(r));      // the split is still there
        Assert.Equal([0, 2, 1], DockPaneLayout.Tabs(r));   // and 2 has a home
    }

    /// <summary>A tab detached into its own window is not dragged back by a revisit.</summary>
    [Fact]
    public void FloatingTab_IsLeftOutEntirely()
    {
        var saved = DockPaneNode.Group(false, DockPaneNode.Pane(0), DockPaneNode.Pane(1));

        var r = DockPaneLayout.Normalise(saved, [0]);      // 1 is floating

        Assert.Equal([0], DockPaneLayout.Tabs(r));
        Assert.True(r!.IsPane);
    }

    [Fact]
    public void EverySurvivingTabAppearsExactlyOnce()
    {
        var saved = DockPaneNode.Group(true,
            DockPaneNode.Pane(3, 0),
            DockPaneNode.Group(false, DockPaneNode.Pane(1), DockPaneNode.Pane(9)));

        var r = DockPaneLayout.Normalise(saved, All(5));

        var placed = DockPaneLayout.Tabs(r).ToList();
        Assert.Equal(placed.Count, placed.Distinct().Count());
        Assert.Equal([0, 1, 2, 3, 4], placed.OrderBy(i => i));
    }

    // ── layouts that are not quite right ──

    /// <summary>
    /// A tab named twice resolves to the first pane that claimed it. Without this
    /// the same document would be added to two panes, and AvalonDock shows it in
    /// neither.
    /// </summary>
    [Fact]
    public void ATabNamedTwice_LandsOnce()
    {
        var saved = DockPaneNode.Group(false, DockPaneNode.Pane(0, 1), DockPaneNode.Pane(1));

        var r = DockPaneLayout.Normalise(saved, All(2));

        Assert.Equal([0, 1], DockPaneLayout.Tabs(r));
        Assert.True(r!.IsPane);          // the second pane emptied and collapsed
    }

    [Fact]
    public void IndicesOutsideTheWorkspace_AreDropped()
    {
        var r = DockPaneLayout.Normalise(DockPaneNode.Pane(-1, 0, 7), All(2));

        Assert.Equal([0, 1], DockPaneLayout.Tabs(r));
    }

    [Fact]
    public void AGroupThatEmptiesCompletely_BecomesOnePaneOfWhatIsLeft()
    {
        var saved = DockPaneNode.Group(false,
            DockPaneNode.Group(true, DockPaneNode.Pane(5), DockPaneNode.Pane(6)),
            DockPaneNode.Pane(0));

        var r = DockPaneLayout.Normalise(saved, [0]);

        Assert.True(r!.IsPane);
        Assert.Equal([0], r.Tabs);
    }

    [Fact]
    public void EmptyPanes_DoNotSurvive()
    {
        var saved = DockPaneNode.Group(false,
            DockPaneNode.Pane(), DockPaneNode.Pane(0), DockPaneNode.Pane());

        var r = DockPaneLayout.Normalise(saved, All(1));

        Assert.True(r!.IsPane);
        Assert.Equal(1, DockPaneLayout.PaneCount(r));
    }

    /// <summary>Normalising twice changes nothing — the second visit is the first.</summary>
    [Fact]
    public void Normalise_IsIdempotent()
    {
        var saved = DockPaneNode.Group(false,
            DockPaneNode.Pane(0, 3), DockPaneNode.Group(true, DockPaneNode.Pane(1), DockPaneNode.Pane(2)));

        var once = DockPaneLayout.Normalise(saved, All(4));
        var twice = DockPaneLayout.Normalise(once, All(4));

        Assert.Equal(DockPaneLayout.Tabs(once), DockPaneLayout.Tabs(twice));
        Assert.Equal(DockPaneLayout.PaneCount(once), DockPaneLayout.PaneCount(twice));
    }

    // ── storage ──

    [Fact]
    public void Json_RoundTripsAShape()
    {
        var saved = DockPaneNode.Group(false,
            DockPaneNode.Pane(0),
            DockPaneNode.Group(true, DockPaneNode.Pane(1, 2), DockPaneNode.Pane(3)));

        var back = DockPaneLayout.FromJson(DockPaneLayout.ToJson(saved));

        Assert.Equal(3, DockPaneLayout.PaneCount(back));
        Assert.Equal([0, 1, 2, 3], DockPaneLayout.Tabs(back));
        Assert.True(back!.Children![1].Vertical);
        Assert.False(back.Vertical);
    }

    /// <summary>
    /// An unsplit workspace stores nothing. Otherwise every workspace would carry a
    /// one-pane layout that pins the next new tab into an arrangement nobody chose.
    /// </summary>
    [Fact]
    public void Json_DoesNotStoreAnUnsplitLayout()
    {
        Assert.Null(DockPaneLayout.ToJson(DockPaneNode.Pane(0, 1, 2)));
        Assert.Null(DockPaneLayout.ToJson(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"Children\":[")]
    public void Json_UnreadableStoredLayout_IsNoLayout(string? stored)
    {
        // The workspace then opens unsplit, which is what it did before any of
        // this existed - never a crash on startup.
        Assert.Null(DockPaneLayout.FromJson(stored));
    }

    /// <summary>The stored form is what gets reconciled, so it has to survive the trip.</summary>
    [Fact]
    public void Json_StoredLayout_StillNormalisesAfterReload()
    {
        var stored = DockPaneLayout.ToJson(
            DockPaneNode.Group(false, DockPaneNode.Pane(0), DockPaneNode.Pane(1)));

        var r = DockPaneLayout.Normalise(DockPaneLayout.FromJson(stored), All(3));

        Assert.Equal(2, DockPaneLayout.PaneCount(r));
        Assert.Equal([0, 2, 1], DockPaneLayout.Tabs(r));
    }

    /// <summary>The remembered layout is not mutated — it is still the fallback next time.</summary>
    [Fact]
    public void Normalise_LeavesTheSavedLayoutAlone()
    {
        var saved = DockPaneNode.Group(false, DockPaneNode.Pane(0), DockPaneNode.Pane(1));

        DockPaneLayout.Normalise(saved, All(4));

        Assert.Equal([0], saved.Children![0].Tabs);
        Assert.Equal([1], saved.Children![1].Tabs);
    }
}
