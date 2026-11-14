namespace Agent.Common.Workspace;

/// <summary>
/// Brings a list that is on screen in line with a fresh listing <b>in place</b>: an item whose
/// key is still there is kept (the same object, so whatever hangs off it — a tree node's
/// expansion, its loaded children, the selection — survives), a gone one is removed, a new one
/// is created at its position. The code view's tree uses it so that expanding a folder re-reads
/// that folder only, and ⟳ no longer rebuilds the whole tree and throws away where you were.
/// </summary>
public static class ListReconcile
{
    /// <summary>What one reconcile changed.</summary>
    public readonly record struct Result(int Added, int Removed, int Kept)
    {
        public bool Changed => Added > 0 || Removed > 0;
    }

    /// <summary>
    /// Make <paramref name="target"/> hold, in order, one item per <paramref name="source"/>
    /// entry. Items are matched by key (<see cref="StringComparer.Ordinal"/>); an item with a
    /// key that matches nothing in the source is removed, as is a second item with the same key.
    /// Only RemoveAt / Insert are used, so an ObservableCollection raises the fewest events
    /// (nothing at all when the listing did not change).
    /// </summary>
    public static Result Reconcile<TItem, TSource>(
        IList<TItem> target,
        IReadOnlyList<TSource> source,
        Func<TItem, string> itemKey,
        Func<TSource, string> sourceKey,
        Func<TSource, TItem> create)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in source) wanted.Add(sourceKey(s));

        // 1) Drop what is gone (and duplicates), keeping the survivors' order.
        var removed = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            var key = itemKey(target[i]);
            if (!wanted.Contains(key))
            {
                target.RemoveAt(i);
                removed++;
            }
        }
        for (var i = 0; i < target.Count; i++)
        {
            if (seen.Add(itemKey(target[i]))) continue;
            target.RemoveAt(i--);
            removed++;
        }

        var existing = new Dictionary<string, TItem>(StringComparer.Ordinal);
        foreach (var item in target) existing[itemKey(item)] = item;

        // 2) Walk the source; position i must hold source[i]'s item.
        var added = 0;
        var kept = 0;
        for (var i = 0; i < source.Count; i++)
        {
            var key = sourceKey(source[i]);
            if (i < target.Count && itemKey(target[i]) == key)
            {
                kept++;
                continue;
            }
            var at = existing.TryGetValue(key, out var item) ? IndexOf(target, itemKey, key, i + 1) : -1;
            if (at >= 0)
            {
                // Out of order (a rename that re-sorted, a case change): move it here.
                target.RemoveAt(at);
                target.Insert(i, item!);
                kept++;
            }
            else
            {
                target.Insert(i, create(source[i]));
                added++;
            }
        }
        return new Result(added, removed, kept);
    }

    private static int IndexOf<TItem>(IList<TItem> list, Func<TItem, string> key, string wanted, int from)
    {
        for (var i = from; i < list.Count; i++)
            if (key(list[i]) == wanted) return i;
        return -1;
    }
}
