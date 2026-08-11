using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Agent.Common.Actors;
using Agent.Common.Data.Entities;

namespace Agent.Common.Orchestration;

/// <summary>
/// Pure conversions between orchestration DB entities and the coordinator's
/// in-memory <see cref="OrchestrationTaskSpec"/> (mission W6 activation). Isolated
/// and WPF-free so it is headlessly testable; the deps list is stored as a JSON
/// array string on <see cref="OrchestrationTask.DependsOnJson"/>.
/// </summary>
public static class OrchestrationMapper
{
    /// <summary>Serializes a dependency key list to the stored JSON array form.</summary>
    public static string SerializeDeps(IReadOnlyList<string> deps)
        => JsonSerializer.Serialize(deps ?? new List<string>());

    /// <summary>Parses the stored JSON array of dependency keys (tolerant of null/blank).</summary>
    public static IReadOnlyList<string> ParseDeps(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json!) ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    /// <summary>Maps a persisted task to a coordinator spec.</summary>
    public static OrchestrationTaskSpec ToSpec(OrchestrationTask t)
        => new(t.TaskKey, t.Prompt, ParseDeps(t.DependsOnJson));

    /// <summary>Maps persisted tasks to coordinator specs.</summary>
    public static IReadOnlyList<OrchestrationTaskSpec> ToSpecs(IEnumerable<OrchestrationTask> tasks)
        => tasks.Select(ToSpec).ToList();

    /// <summary>Builds a persisted task from a spec (RunId set by the caller).</summary>
    public static OrchestrationTask ToEntity(OrchestrationTaskSpec spec)
        => new()
        {
            TaskKey = spec.TaskKey,
            Prompt = spec.Prompt,
            DependsOnJson = SerializeDeps(spec.DependsOn),
            Status = "pending",
        };
}
