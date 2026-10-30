// Port of kitten_text_processing/_fst.py (Apache-2.0 AND MIT): byte-level tropical weighted transducers.
// Kept line for line where order matters: arc contraction, the frontier decoder, and the OpenFst-ordered lattice
// (reverse DFS finishing order, trim, RmEpsilon-style output projection) decide which of equal-weight readings
// wins. Weights are float32 and every addition is rounded to float32, as the Python does with struct pack/unpack
// (a float+float in C# is already the correctly rounded float32 sum).
using System.IO.Compression;
using System.Text;

namespace Agent.Common.Voice.KittenTts2.TextNorm;

internal sealed class NoPathException(string message) : InvalidOperationException(message);

internal sealed class Fst
{
    private readonly bool _projectOutput;
    private readonly int _start;
    private readonly uint[] _offsets, _labels, _targets;
    private readonly float[] _finals, _weights;
    private byte[]? _indegree;
    private readonly Dictionary<int, Dictionary<int, List<Arc>>> _index = new();

    private readonly record struct Arc(int Target, float Weight, byte[] Suffix);

    public Fst(string path, bool projectOutput = false)
    {
        _projectOutput = projectOutput;
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        var data = ms.ToArray();
        if (Encoding.ASCII.GetString(data, 0, 8) != "KITTEN1\0") throw new InvalidDataException($"Invalid grammar: {path}");
        _start = (int)BitConverter.ToUInt32(data, 8);
        var states = (int)BitConverter.ToUInt32(data, 12);
        var arcs = (int)BitConverter.ToUInt32(data, 16);
        var at = 20;
        _offsets = ReadU(data, ref at, states + 1);
        _finals = ReadF(data, ref at, states);
        _labels = ReadU(data, ref at, arcs);
        _targets = ReadU(data, ref at, arcs);
        _weights = ReadF(data, ref at, arcs);
    }

    private static uint[] ReadU(byte[] d, ref int at, int n)
    {
        var a = new uint[n];
        Buffer.BlockCopy(d, at, a, 0, n * 4);
        at += n * 4;
        return a;
    }

    private static float[] ReadF(byte[] d, ref int at, int n)
    {
        var a = new float[n];
        Buffer.BlockCopy(d, at, a, 0, n * 4);
        at += n * 4;
        return a;
    }

    private static float Add(float cost, float weight) => weight != 0 ? cost + weight : cost;

    private Dictionary<int, List<Arc>> Arcs(int state)
    {
        if (_index.TryGetValue(state, out var index)) return index;
        if (_indegree is null)
        {
            _indegree = new byte[_finals.Length];
            foreach (var t in _targets)
                if (_indegree[t] < 2) _indegree[t]++;
        }
        index = new Dictionary<int, List<Arc>>();
        for (var i = (int)_offsets[state]; i < (int)_offsets[state + 1]; i++)
        {
            var label = _labels[i];
            var target = (int)_targets[i];
            var suffix = new List<byte>();
            if ((label >> 8) != 0) suffix.Add((byte)(label >> 8));
            // Contract deterministic zero-cost output-only chains; never through a join or a nonzero weight.
            var visited = new HashSet<int>();
            while (!_projectOutput && _indegree[target] == 1 && !float.IsFinite(_finals[target]) && !visited.Contains(target))
            {
                int s = (int)_offsets[target], e = (int)_offsets[target + 1];
                if (e != s + 1 || (_labels[s] & 255) != 0 || _weights[s] != 0) break;
                if (_indegree[_targets[s]] != 1) break;
                visited.Add(target);
                var output = _labels[s] >> 8;
                if (output != 0) suffix.Add((byte)output);
                target = (int)_targets[s];
            }
            var key = (int)(label & 255);
            if (!index.TryGetValue(key, out var list)) index[key] = list = [];
            list.Add(new Arc(target, _weights[i], suffix.ToArray()));
        }
        if (_index.Count >= 50000) _index.Clear();
        _index[state] = index;
        return index;
    }

    private static readonly List<Arc> NoArcs = [];

    private List<Arc> ArcsFor(int state, int label) => Arcs(state).TryGetValue(label, out var l) ? l : NoArcs;

    private sealed record Entry(float Cost, byte[] Output, bool Ambiguous);

    public string Rewrite(string text)
    {
        text = text.Replace("\0", "");
        if (_projectOutput) return RewriteOrdered(text);
        var active = new Dictionary<int, Entry> { [_start] = new(0f, [], false) };
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var ci = 0; ci <= bytes.Length; ci++)
        {
            // epsilon closure
            var queue = new Queue<int>(active.Keys);
            var queued = new HashSet<int>(active.Keys);
            while (queue.Count > 0)
            {
                var state = queue.Dequeue();
                queued.Remove(state);
                var cur = active[state];
                foreach (var arc in ArcsFor(state, 0))
                {
                    var candidate = Add(cur.Cost, arc.Weight);
                    var value = Concat(cur.Output, arc.Suffix);
                    if (!active.TryGetValue(arc.Target, out var old) || candidate < old.Cost)
                        active[arc.Target] = new Entry(candidate, value, cur.Ambiguous);
                    else if (candidate == old.Cost && !old.Ambiguous && (cur.Ambiguous || !Same(value, old.Output)))
                        active[arc.Target] = old with { Ambiguous = true };
                    else
                        continue;
                    if (queued.Add(arc.Target)) queue.Enqueue(arc.Target);
                }
            }
            if (ci == bytes.Length)
            {
                Entry? best = null;
                foreach (var (state, e) in active)
                {
                    var final = _finals[state];
                    if (!float.IsFinite(final)) continue;
                    var candidate = e.Cost + final;
                    if (best is null || candidate < best.Cost) best = new Entry(candidate, e.Output, e.Ambiguous);
                    else if (candidate == best.Cost && (e.Ambiguous || !Same(e.Output, best.Output))) best = best with { Ambiguous = true };
                }
                if (best is null) throw new NoPathException($"No accepting path for {Head(text)}");
                return best.Ambiguous ? RewriteOrdered(text) : Encoding.UTF8.GetString(best.Output);
            }
            var following = new Dictionary<int, Entry>();
            int ch = bytes[ci];
            foreach (var (state, e) in active)
            {
                foreach (var arc in ArcsFor(state, ch))
                {
                    var candidate = Add(e.Cost, arc.Weight);
                    var value = Concat(e.Output, arc.Suffix);
                    if (!following.TryGetValue(arc.Target, out var old) || candidate < old.Cost)
                        following[arc.Target] = new Entry(candidate, value, e.Ambiguous);
                    else if (candidate == old.Cost && !old.Ambiguous && (e.Ambiguous || !Same(value, old.Output)))
                        following[arc.Target] = old with { Ambiguous = true };
                }
            }
            if (following.Count == 0) throw new NoPathException($"No accepting path for {Head(text)}");
            active = following;
        }
        throw new System.Diagnostics.UnreachableException();
    }

    // ── the ordered lattice ──

    private readonly record struct Edge(long Target, float Weight, byte[] Output);

    private static long Node(int position, int state) => ((long)position << 32) | (uint)state;
    private static int Pos(long node) => (int)(node >> 32);
    private static int St(long node) => (int)(node & 0xffffffff);

    private string RewriteOrdered(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var length = data.Length;
        var start = Node(0, _start);
        var adjacency = new Dictionary<long, List<Edge>>();

        List<Edge> Edges(long node)
        {
            int position = Pos(node), state = St(node);
            var arcs = Arcs(state);
            var outList = new List<Edge>();
            if (arcs.TryGetValue(0, out var eps))
                foreach (var a in eps) outList.Add(new Edge(Node(position, a.Target), a.Weight, a.Suffix));
            if (position < length && arcs.TryGetValue(data[position], out var step))
                foreach (var a in step) outList.Add(new Edge(Node(position + 1, a.Target), a.Weight, a.Suffix));
            adjacency[node] = outList;
            return outList;
        }

        var seen = new HashSet<long> { start };
        var visiting = new HashSet<long> { start };
        var stack = new List<(long Node, List<Edge> Edges, int Next)> { (start, Edges(start), 0) };
        var ordered = new List<long>();
        var live = new HashSet<long>();
        var cyclic = false;
        while (stack.Count > 0)
        {
            var (node, edges, next) = stack[^1];
            if (next >= edges.Count)
            {
                ordered.Add(node);
                if ((Pos(node) == length && float.IsFinite(_finals[St(node)])) || adjacency[node].Any(e => live.Contains(e.Target)))
                    live.Add(node);
                visiting.Remove(node);
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack[^1] = (node, edges, next + 1);
            var target = edges[next].Target;
            if (visiting.Contains(target)) cyclic = true;
            if (seen.Add(target))
            {
                visiting.Add(target);
                stack.Add((target, Edges(target), 0));
            }
        }
        if (cyclic)
        {
            var reverse = new Dictionary<long, List<long>>();
            foreach (var (node, outgoing) in adjacency)
                foreach (var e in outgoing)
                {
                    if (!reverse.TryGetValue(e.Target, out var l)) reverse[e.Target] = l = [];
                    l.Add(node);
                }
            var pendingLive = new Stack<long>(live);
            while (pendingLive.Count > 0)
                if (reverse.TryGetValue(pendingLive.Pop(), out var parents0))
                    foreach (var parent in parents0)
                        if (live.Add(parent)) pendingLive.Push(parent);
        }
        if (!live.Contains(start)) throw new NoPathException($"No accepting path for {Head(text)}");
        var finalWeights = new Dictionary<long, float>();
        foreach (var node in live)
            if (Pos(node) == length) finalWeights[node] = _finals[St(node)];
        if (_projectOutput)
        {
            ordered = ProjectLattice(adjacency, finalWeights, ordered, live, start);
            live = new HashSet<long>(ordered);
        }

        var costs = new Dictionary<long, float> { [start] = 0f };
        var parents = new Dictionary<long, (long Node, byte[] Output)>();
        var best = float.PositiveInfinity;
        long? finalNode = null;
        var pending = new Queue<long>();
        var queued = new HashSet<long>();
        for (var i = ordered.Count - 1; i >= 0; i--)
            if (live.Contains(ordered[i]) && queued.Add(ordered[i])) pending.Enqueue(ordered[i]);
        while (pending.Count > 0)
        {
            var node = pending.Dequeue();
            queued.Remove(node);
            var cost = costs.TryGetValue(node, out var c) ? c : float.PositiveInfinity;
            if (!float.IsFinite(cost)) continue;
            var fw = finalWeights.TryGetValue(node, out var f) ? f : float.PositiveInfinity;
            if (float.IsFinite(fw))
            {
                var candidate = cost + fw;
                if (candidate < best) { best = candidate; finalNode = node; }
            }
            foreach (var e in adjacency[node])
            {
                if (!live.Contains(e.Target)) continue;
                var candidate = Add(cost, e.Weight);
                if (candidate < (costs.TryGetValue(e.Target, out var old) ? old : float.PositiveInfinity))
                {
                    costs[e.Target] = candidate;
                    parents[e.Target] = (node, e.Output);
                    if (queued.Add(e.Target)) pending.Enqueue(e.Target);
                }
            }
        }
        if (finalNode is null) throw new NoPathException($"No accepting path for {Head(text)}");
        var pieces = new List<byte[]>();
        var at = finalNode.Value;
        while (at != start)
        {
            var (prev, output) = parents[at];
            pieces.Add(output);
            at = prev;
        }
        pieces.Reverse();
        return Encoding.UTF8.GetString(pieces.SelectMany(b => b).ToArray());
    }

    /// <summary>Output projection + epsilon removal as Pynini's top_rewrite does it (RmEpsilon insertion order, reversed).</summary>
    private static List<long> ProjectLattice(Dictionary<long, List<Edge>> adjacency, Dictionary<long, float> finals,
        List<long> ordered, HashSet<long> live, long start)
    {
        var incoming = new HashSet<long> { start };
        foreach (var node in live)
            foreach (var e in adjacency[node])
                if (e.Output.Length > 0 && live.Contains(e.Target)) incoming.Add(e.Target);
        foreach (var source in ordered)
        {
            if (!live.Contains(source) || !incoming.Contains(source)) continue;
            var distances = new Dictionary<long, float> { [source] = 0f };
            var pending = new Queue<long>();
            pending.Enqueue(source);
            var queued = new HashSet<long> { source };
            while (pending.Count > 0)
            {
                var node = pending.Dequeue();
                queued.Remove(node);
                foreach (var e in adjacency[node])
                {
                    if (e.Output.Length > 0 || !live.Contains(e.Target)) continue;
                    var distance = Add(distances[node], e.Weight);
                    if (distance < (distances.TryGetValue(e.Target, out var d) ? d : float.PositiveInfinity))
                    {
                        distances[e.Target] = distance;
                        if (queued.Add(e.Target)) pending.Enqueue(e.Target);
                    }
                }
            }
            var expandedKeys = new List<(long Target, string Key, byte[] Output)>();
            var expanded = new Dictionary<(long, string), float>();
            var final = float.PositiveInfinity;
            var stack = new Stack<long>();
            stack.Push(source);
            var seen = new HashSet<long>();
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (!seen.Add(node)) continue;
                var distance = distances[node];
                final = Math.Min(final, Add(distance, finals.TryGetValue(node, out var fw) ? fw : float.PositiveInfinity));
                foreach (var e in adjacency[node])
                {
                    if (!live.Contains(e.Target)) continue;
                    if (e.Output.Length == 0)
                    {
                        if (!seen.Contains(e.Target)) stack.Push(e.Target);
                    }
                    else
                    {
                        var key = (e.Target, Convert.ToBase64String(e.Output));
                        var w = Add(distance, e.Weight);
                        if (expanded.TryGetValue(key, out var old)) expanded[key] = Math.Min(old, w);
                        else { expanded[key] = w; expandedKeys.Add((e.Target, key.Item2, e.Output)); }
                    }
                }
            }
            var rebuilt = new List<Edge>();
            for (var i = expandedKeys.Count - 1; i >= 0; i--)
            {
                var (t, k, output) = expandedKeys[i];
                rebuilt.Add(new Edge(t, expanded[(t, k)], output));
            }
            adjacency[source] = rebuilt;
            finals[source] = final;
        }
        var seen2 = new HashSet<long> { start };
        var order = new List<long>();
        var dfs = new List<(long Node, int Next)> { (start, 0) };
        while (dfs.Count > 0)
        {
            var (node, next) = dfs[^1];
            var arcs = adjacency[node];
            if (next >= arcs.Count)
            {
                order.Add(node);
                dfs.RemoveAt(dfs.Count - 1);
                continue;
            }
            dfs[^1] = (node, next + 1);
            if (seen2.Add(arcs[next].Target)) dfs.Add((arcs[next].Target, 0));
        }
        return order;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        if (b.Length == 0) return a;
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    private static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

    private static string Head(string s) => s.Length > 80 ? s[..80] : s;
}

