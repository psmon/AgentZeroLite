using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Agent.Common.Actors;
using Agent.Common.Data.Entities;
using Agent.Common.Llm.Tools;
using Agent.Common.Orchestration;

namespace ZeroCommon.Tests;

/// <summary>
/// Headless tests for W6 activation: the pure deps mapper and the
/// coordinator↔worker-router↔worker wiring (end-to-end, with probe workers).
/// </summary>
[Trait("Category", "OrchestrationActivation")]
public sealed class OrchestrationMapperTests
{
    [Fact]
    public void Deps_RoundTrip()
    {
        var json = OrchestrationMapper.SerializeDeps(new[] { "a", "b" });
        var parsed = OrchestrationMapper.ParseDeps(json);
        Assert.Equal(new[] { "a", "b" }, parsed);
    }

    [Fact]
    public void ParseDeps_NullOrBlank_ReturnsEmpty()
    {
        Assert.Empty(OrchestrationMapper.ParseDeps(null));
        Assert.Empty(OrchestrationMapper.ParseDeps(""));
        Assert.Empty(OrchestrationMapper.ParseDeps("not json"));
    }

    [Fact]
    public void ToSpec_MapsEntityFields()
    {
        var t = new OrchestrationTask { TaskKey = "x", Prompt = "do x", DependsOnJson = "[\"a\"]" };
        var spec = OrchestrationMapper.ToSpec(t);
        Assert.Equal("x", spec.TaskKey);
        Assert.Equal("do x", spec.Prompt);
        Assert.Equal(new[] { "a" }, spec.DependsOn);
    }

    [Fact]
    public void ToEntity_SerializesDeps()
    {
        var spec = new OrchestrationTaskSpec("x", "p", new[] { "a", "b" });
        var e = OrchestrationMapper.ToEntity(spec);
        Assert.Equal("x", e.TaskKey);
        Assert.Contains("a", e.DependsOnJson);
        Assert.Equal("pending", e.Status);
    }
}

public sealed class WorkerRouterActorTests : TestKit
{
    [Fact]
    public void Router_RoundRobins_AndPreservesCoordinatorAsSender()
    {
        var w1 = CreateTestProbe("w1");
        var w2 = CreateTestProbe("w2");
        var router = Sys.ActorOf(WorkerRouterActor.Props(new[] { w1.Ref, w2.Ref }), "router");

        // TestActor stands in for the coordinator (the sender of the dispatch).
        router.Tell(new DispatchTaskToWorker("a", "do a"), TestActor);
        router.Tell(new DispatchTaskToWorker("b", "do b"), TestActor);

        var m1 = w1.ExpectMsg<DispatchTaskToWorker>();
        var m2 = w2.ExpectMsg<DispatchTaskToWorker>();
        Assert.Equal("a", m1.TaskKey);
        Assert.Equal("b", m2.TaskKey);
        // Forwarded → the coordinator (TestActor) remains the reply target.
        Assert.Equal(TestActor, w1.LastSender);
    }

    [Fact]
    public void EndToEnd_CoordinatorRouterProbeWorkers_CompletesRun()
    {
        // Two probe "workers" that immediately reply WorkerDone to the coordinator.
        var worker = CreateTestProbe("worker");
        var router = Sys.ActorOf(WorkerRouterActor.Props(new[] { worker.Ref }), "router-e2e");
        var coord = Sys.ActorOf(Props.Create(() => new CoordinatorActor(router)), "coord-e2e");

        coord.Tell(new StartOrchestrationRun("run", new[]
        {
            new OrchestrationTaskSpec("a", "do a", new string[0]),
            new OrchestrationTaskSpec("b", "do b", new[] { "a" }),
        }), TestActor);

        // Worker gets 'a' (via router, sender=coordinator), replies done → 'b' dispatched.
        var d1 = worker.ExpectMsg<DispatchTaskToWorker>();
        Assert.Equal("a", d1.TaskKey);
        worker.LastSender.Tell(new WorkerDone("a", true, "ok"));

        var d2 = worker.ExpectMsg<DispatchTaskToWorker>();
        Assert.Equal("b", d2.TaskKey);
        worker.LastSender.Tell(new WorkerDone("b", true, "ok"));

        var done = ExpectMsg<OrchestrationRunCompleted>();
        Assert.True(done.Success);
        Assert.Equal(2, done.Completed);
    }
}

/// <summary>
/// Live wiring: coordinator → router → WorkerSinkActor → (mock terminal) →
/// WorkerDone → run completes. Proves the full orchestration loop drives a
/// terminal-agent abstraction end to end without any real agent.
/// </summary>
public sealed class WorkerSinkActorTests : TestKit
{
    // Mock terminal agent: records prompts sent, returns a constant reply so the
    // sink's idle-detection stabilizes immediately.
    private sealed class MockTerminalToolbelt : IAgentToolbelt
    {
        public readonly List<string> Sent = new();
        public Task<string> ListTerminalsAsync(CancellationToken ct) => Task.FromResult("{}");
        public Task<string> ReadTerminalAsync(int g, int t, int n, CancellationToken ct) => Task.FromResult("agent reply: done");
        public Task<bool> SendToTerminalAsync(int g, int t, string text, CancellationToken ct) { lock (Sent) Sent.Add(text); return Task.FromResult(true); }
        public Task<bool> SendKeyAsync(int g, int t, string key, CancellationToken ct) => Task.FromResult(true);
    }

    private static readonly WorkerSinkOptions Fast =
        new() { PollDelay = TimeSpan.FromMilliseconds(10), StableRounds = 1, MaxReads = 5, ReadChars = 500 };

    [Fact]
    public void Sink_SendsPrompt_ReadsIdle_ReportsDone()
    {
        var tb = new MockTerminalToolbelt();
        var sink = Sys.ActorOf(Props.Create(() => new WorkerSinkActor(tb, 0, 0, Fast)), "sink");

        sink.Tell(new DispatchTaskToWorker("a", "do a"), TestActor);
        var done = ExpectMsg<WorkerDone>(TimeSpan.FromSeconds(5));

        Assert.Equal("a", done.TaskKey);
        Assert.True(done.Success);
        Assert.Contains("do a", tb.Sent);
        Assert.Contains("done", done.Message);
    }

    [Fact]
    public void EndToEnd_Coordinator_Router_Sink_CompletesDagOnMockTerminals()
    {
        var tb = new MockTerminalToolbelt();
        var sink0 = Sys.ActorOf(Props.Create(() => new WorkerSinkActor(tb, 0, 0, Fast)), "e2e-sink0");
        var sink1 = Sys.ActorOf(Props.Create(() => new WorkerSinkActor(tb, 0, 1, Fast)), "e2e-sink1");
        var router = Sys.ActorOf(WorkerRouterActor.Props(new[] { sink0, sink1 }), "e2e-sink-router");
        var coord = Sys.ActorOf(Props.Create(() => new CoordinatorActor(router)), "e2e-sink-coord");

        coord.Tell(new StartOrchestrationRun("live", new[]
        {
            new OrchestrationTaskSpec("a", "task a", new string[0]),
            new OrchestrationTaskSpec("b", "task b", new[] { "a" }),
        }), TestActor);

        // No manual WorkerDone here — the sinks drive real completion via the
        // mock terminal. The whole DAG should finish on its own.
        var done = ExpectMsg<OrchestrationRunCompleted>(TimeSpan.FromSeconds(10));
        Assert.True(done.Success);
        Assert.Equal(2, done.Completed);
        Assert.Contains("task a", tb.Sent);
        Assert.Contains("task b", tb.Sent);
    }
}
