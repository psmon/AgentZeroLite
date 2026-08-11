using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Agent.Common.Actors;
using Agent.Common.Orchestration;

namespace ZeroCommon.Tests;

/// <summary>
/// Pure DAG-resolver tests for W6 orchestration (headless, no actors).
/// </summary>
[Trait("Category", "Orchestration")]
public sealed class OrchestrationDagTests
{
    private static OrchestrationDag.Node N(string id, params string[] deps)
        => new(id, deps);

    [Fact]
    public void ReadyTasks_NoDeps_AllReady()
    {
        var tasks = new[] { N("a"), N("b") };
        var ready = OrchestrationDag.ReadyTasks(tasks, new HashSet<string>());
        Assert.Equal(new[] { "a", "b" }, ready);
    }

    [Fact]
    public void ReadyTasks_BlockedUntilDepCompletes()
    {
        var tasks = new[] { N("a"), N("b", "a") };
        var completed = new HashSet<string>();
        Assert.Equal(new[] { "a" }, OrchestrationDag.ReadyTasks(tasks, completed));

        completed.Add("a");
        Assert.Equal(new[] { "b" }, OrchestrationDag.ReadyTasks(tasks, completed));
    }

    [Fact]
    public void ReadyTasks_SkipsInFlightAndCompleted()
    {
        var tasks = new[] { N("a"), N("b") };
        var ready = OrchestrationDag.ReadyTasks(
            tasks, new HashSet<string> { "a" }, new HashSet<string> { "b" });
        Assert.Empty(ready);
    }

    [Fact]
    public void HasCycle_DetectsCycle()
    {
        var tasks = new List<OrchestrationDag.Node> { N("a", "b"), N("b", "a") };
        Assert.True(OrchestrationDag.HasCycle(tasks, out _));
    }

    [Fact]
    public void HasCycle_AcyclicIsFalse()
    {
        var tasks = new List<OrchestrationDag.Node> { N("a"), N("b", "a"), N("c", "a", "b") };
        Assert.False(OrchestrationDag.HasCycle(tasks, out var unknown));
        Assert.Empty(unknown);
    }

    [Fact]
    public void HasCycle_ReportsUnknownDeps()
    {
        var tasks = new List<OrchestrationDag.Node> { N("a", "ghost") };
        OrchestrationDag.HasCycle(tasks, out var unknown);
        Assert.Contains("ghost", unknown);
    }

    [Fact]
    public void AllComplete_TrueOnlyWhenEveryTaskDone()
    {
        var tasks = new[] { N("a"), N("b") };
        Assert.False(OrchestrationDag.AllComplete(tasks, new HashSet<string> { "a" }));
        Assert.True(OrchestrationDag.AllComplete(tasks, new HashSet<string> { "a", "b" }));
    }
}

/// <summary>
/// CoordinatorActor protocol tests via Akka.TestKit (headless). A test probe
/// stands in for the worker router; the coordinator's dispatch/advance/complete
/// behavior is verified without any real agent.
/// </summary>
public sealed class CoordinatorActorTests : TestKit
{
    private static OrchestrationTaskSpec T(string key, string prompt, params string[] deps)
        => new(key, prompt, deps);

    [Fact]
    public void LinearChain_DispatchesInDependencyOrder()
    {
        var worker = CreateTestProbe("worker");
        var coord = Sys.ActorOf(Props.Create(() => new CoordinatorActor(worker.Ref)), "coord-linear");

        coord.Tell(new StartOrchestrationRun("chain", new[]
        {
            T("a", "do a"),
            T("b", "do b", "a"),
        }), TestActor);

        // Only 'a' is ready first.
        var d1 = worker.ExpectMsg<DispatchTaskToWorker>();
        Assert.Equal("a", d1.TaskKey);
        worker.ExpectNoMsg(200);

        // Completing 'a' unlocks 'b'.
        coord.Tell(new WorkerDone("a", true, "ok"));
        var d2 = worker.ExpectMsg<DispatchTaskToWorker>();
        Assert.Equal("b", d2.TaskKey);

        // Completing 'b' finishes the run.
        coord.Tell(new WorkerDone("b", true, "ok"));
        var done = ExpectMsg<OrchestrationRunCompleted>();
        Assert.True(done.Success);
        Assert.Equal(2, done.Completed);
    }

    [Fact]
    public void IndependentTasks_DispatchedInParallel()
    {
        var worker = CreateTestProbe("worker");
        var coord = Sys.ActorOf(Props.Create(() => new CoordinatorActor(worker.Ref)), "coord-parallel");

        coord.Tell(new StartOrchestrationRun("fanout", new[]
        {
            T("a", "do a"),
            T("b", "do b"),
        }), TestActor);

        var keys = new[] { worker.ExpectMsg<DispatchTaskToWorker>().TaskKey,
                           worker.ExpectMsg<DispatchTaskToWorker>().TaskKey };
        Assert.Contains("a", keys);
        Assert.Contains("b", keys);

        coord.Tell(new WorkerDone("a", true, "ok"));
        coord.Tell(new WorkerDone("b", true, "ok"));
        var done = ExpectMsg<OrchestrationRunCompleted>();
        Assert.True(done.Success);
    }

    [Fact]
    public void FailedDependency_BlocksDependentAndRunFinishesWithFailure()
    {
        var worker = CreateTestProbe("worker");
        var coord = Sys.ActorOf(Props.Create(() => new CoordinatorActor(worker.Ref)), "coord-fail");

        coord.Tell(new StartOrchestrationRun("failchain", new[]
        {
            T("a", "do a"),
            T("b", "do b", "a"),
        }), TestActor);

        worker.ExpectMsg<DispatchTaskToWorker>(); // a
        coord.Tell(new WorkerDone("a", false, "boom"));

        // 'b' never becomes ready (dep failed); run ends unsuccessful.
        worker.ExpectNoMsg(200);
        var done = ExpectMsg<OrchestrationRunCompleted>();
        Assert.False(done.Success);
        Assert.Equal(1, done.Failed);
    }

    [Fact]
    public void CyclicRun_AbortsImmediately()
    {
        var worker = CreateTestProbe("worker");
        var coord = Sys.ActorOf(Props.Create(() => new CoordinatorActor(worker.Ref)), "coord-cycle");

        coord.Tell(new StartOrchestrationRun("cycle", new[]
        {
            T("a", "do a", "b"),
            T("b", "do b", "a"),
        }), TestActor);

        worker.ExpectNoMsg(200);
        var done = ExpectMsg<OrchestrationRunCompleted>();
        Assert.False(done.Success);
    }

    [Fact]
    public void AskCoordinator_GetsProceedReply()
    {
        var worker = CreateTestProbe("worker");
        var coord = Sys.ActorOf(Props.Create(() => new CoordinatorActor(worker.Ref)), "coord-ask");

        coord.Tell(new AskCoordinator("a", "may I continue?"), TestActor);
        var reply = ExpectMsg<AskCoordinatorReply>();
        Assert.Equal("proceed", reply.Answer);
    }

    [Fact]
    public void QueryRunStatus_ReportsProgress()
    {
        var worker = CreateTestProbe("worker");
        var coord = Sys.ActorOf(Props.Create(() => new CoordinatorActor(worker.Ref)), "coord-status");

        coord.Tell(new StartOrchestrationRun("s", new[] { T("a", "a"), T("b", "b", "a") }), TestActor);
        worker.ExpectMsg<DispatchTaskToWorker>();
        coord.Tell(new WorkerDone("a", true, "ok"));
        worker.ExpectMsg<DispatchTaskToWorker>();

        coord.Tell(new QueryRunStatus(), TestActor);
        var status = ExpectMsg<RunStatusReply>();
        Assert.Equal(2, status.Total);
        Assert.Equal(1, status.Completed);
        Assert.Equal(1, status.InFlight);
    }
}
