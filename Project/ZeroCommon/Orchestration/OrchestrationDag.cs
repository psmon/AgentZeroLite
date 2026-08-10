using System.Collections.Generic;
using System.Linq;

namespace Agent.Common.Orchestration;

/// <summary>
/// Pure (WPF-free) dependency-graph logic for supervised multi-agent
/// orchestration (mission W6, orca-adoption). Given a set of tasks with
/// dependencies and the set of already-completed task ids, computes which tasks
/// are ready to dispatch, and detects cycles. Lives in ZeroCommon so the DAG
/// reasoning is headlessly testable; the coordinator actor drives it.
/// </summary>
public static class OrchestrationDag
{
    /// <summary>A task node: an id plus the ids it depends on.</summary>
    public sealed record Node(string Id, IReadOnlyList<string> DependsOn);

    /// <summary>
    /// Returns the ids of tasks that are ready to run: not yet completed, not
    /// already in <paramref name="inFlight"/>, and with every dependency
    /// completed. Order follows the input order for determinism.
    /// </summary>
    public static IReadOnlyList<string> ReadyTasks(
        IEnumerable<Node> tasks,
        ISet<string> completed,
        ISet<string>? inFlight = null)
    {
        inFlight ??= new HashSet<string>();
        var ready = new List<string>();
        foreach (var t in tasks)
        {
            if (completed.Contains(t.Id) || inFlight.Contains(t.Id)) continue;
            if (t.DependsOn.All(completed.Contains))
                ready.Add(t.Id);
        }
        return ready;
    }

    /// <summary>
    /// True if the graph has a cycle (which would deadlock the run). Uses
    /// Kahn's algorithm: if a topological order can't consume every node, a
    /// cycle exists. Unknown dependency ids are treated as never-satisfiable
    /// and reported via <paramref name="unknownDeps"/>.
    /// </summary>
    public static bool HasCycle(IReadOnlyList<Node> tasks, out IReadOnlyList<string> unknownDeps)
    {
        var ids = new HashSet<string>(tasks.Select(t => t.Id));
        var unknown = new List<string>();
        foreach (var t in tasks)
            foreach (var d in t.DependsOn)
                if (!ids.Contains(d))
                    unknown.Add(d);
        unknownDeps = unknown;

        // Kahn's: repeatedly remove nodes whose deps are all resolved.
        var resolved = new HashSet<string>();
        bool progress = true;
        while (progress)
        {
            progress = false;
            foreach (var t in tasks)
            {
                if (resolved.Contains(t.Id)) continue;
                // A dep that is unknown can never resolve → node stays stuck,
                // but that is "unresolvable", handled separately; for cycle
                // detection we only consider known deps.
                if (t.DependsOn.Where(ids.Contains).All(resolved.Contains))
                {
                    resolved.Add(t.Id);
                    progress = true;
                }
            }
        }
        return resolved.Count != tasks.Count;
    }

    /// <summary>
    /// True once every task id is in <paramref name="completed"/> (the run is done).
    /// </summary>
    public static bool AllComplete(IEnumerable<Node> tasks, ISet<string> completed)
        => tasks.All(t => completed.Contains(t.Id));
}
