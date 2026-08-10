using System;
using System.Collections.Generic;
using System.Linq;
using Agent.Common.Actors;
using Agent.Common.Data;
using Agent.Common.Data.Entities;

namespace Agent.Common.Orchestration;

/// <summary>
/// Persists orchestration runs so a supervised multi-agent run survives restart
/// (mission W6 activation). Thin EF layer over <see cref="AppDbContext"/>; the
/// task/spec conversions go through the pure <see cref="OrchestrationMapper"/>.
/// </summary>
public static class OrchestrationStore
{
    /// <summary>Creates a run with its tasks and returns the new run id.</summary>
    public static int CreateRun(AppDbContext db, string name, IReadOnlyList<OrchestrationTaskSpec> specs)
    {
        var run = new OrchestrationRun { Name = name, Status = "pending", CreatedAtUtc = DateTime.UtcNow };
        run.Tasks = specs.Select(OrchestrationMapper.ToEntity).ToList();
        db.OrchestrationRuns.Add(run);
        db.SaveChanges();
        return run.Id;
    }

    /// <summary>Loads a run's tasks as coordinator specs.</summary>
    public static IReadOnlyList<OrchestrationTaskSpec> LoadSpecs(AppDbContext db, int runId)
    {
        var tasks = db.OrchestrationTasks.Where(t => t.RunId == runId).ToList();
        return OrchestrationMapper.ToSpecs(tasks);
    }

    /// <summary>Records a task's completion/failure.</summary>
    public static void MarkTask(AppDbContext db, int runId, string taskKey, bool success, string result)
    {
        var task = db.OrchestrationTasks.FirstOrDefault(t => t.RunId == runId && t.TaskKey == taskKey);
        if (task is null) return;
        task.Status = success ? "done" : "failed";
        task.ResultMessage = result;
        task.FinishedAtUtc = DateTime.UtcNow;
        db.SaveChanges();
    }

    /// <summary>Sets the run's terminal status.</summary>
    public static void FinishRun(AppDbContext db, int runId, bool success)
    {
        var run = db.OrchestrationRuns.FirstOrDefault(r => r.Id == runId);
        if (run is null) return;
        run.Status = success ? "done" : "failed";
        run.FinishedAtUtc = DateTime.UtcNow;
        db.SaveChanges();
    }
}
