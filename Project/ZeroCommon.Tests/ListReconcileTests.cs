using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Agent.Common.Workspace;
using Xunit;

namespace ZeroCommon.Tests;

/// <summary>The in-place merge the code view's tree uses to refresh one folder without rebuilding it.</summary>
public sealed class ListReconcileTests
{
    private sealed class Item(string key)
    {
        public string Key { get; } = key;
    }

    private static ListReconcile.Result Run(IList<Item> target, params string[] source)
        => ListReconcile.Reconcile(target, source, i => i.Key, s => s, s => new Item(s));

    [Fact]
    public void Keeps_survivors_as_the_same_objects_and_inserts_new_ones_in_place()
    {
        var a = new Item("a");
        var c = new Item("c");
        var list = new List<Item> { a, new("b"), c };

        var result = Run(list, "a", "c", "d", "e");

        Assert.Equal(new[] { "a", "c", "d", "e" }, list.Select(i => i.Key));
        Assert.Same(a, list[0]);
        Assert.Same(c, list[1]);
        Assert.Equal((2, 1, 2), (result.Added, result.Removed, result.Kept));
    }

    [Fact]
    public void An_unchanged_listing_raises_no_collection_events()
    {
        var list = new ObservableCollection<Item> { new("a"), new("b") };
        var events = 0;
        list.CollectionChanged += (_, _) => events++;

        var result = Run(list, "a", "b");

        Assert.False(result.Changed);
        Assert.Equal(0, events);
    }

    [Fact]
    public void Reorders_and_drops_duplicates()
    {
        var b = new Item("b");
        var list = new List<Item> { new("a"), b, new("a"), new("x") };

        Run(list, "b", "a");

        Assert.Equal(new[] { "b", "a" }, list.Select(i => i.Key));
        Assert.Same(b, list[0]);
    }

    [Fact]
    public void A_placeholder_with_an_empty_key_is_replaced()
    {
        var list = new ObservableCollection<Item> { new("") };
        Run(list, "x", "y");
        Assert.Equal(new[] { "x", "y" }, list.Select(i => i.Key));

        var events = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => events.Add(e.Action);
        Run(list);
        Assert.Empty(list);
        Assert.All(events, e => Assert.Equal(NotifyCollectionChangedAction.Remove, e));
    }
}
