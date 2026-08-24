using Agent.Common.Remote;
using Agent.Common.Services;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Xunit;

namespace ZeroCommon.Tests.Remote;

/// <summary>
/// Actor-level tests for the Remote intermediary: <see cref="RemoteSessionActor"/> relays
/// terminal output to the web callback and injects web input into the terminal;
/// <see cref="RemoteHubActor"/> enforces the connection cap.
/// </summary>
public sealed class RemoteActorTests : TestKit
{
    [Fact]
    public void Session_sends_snapshot_then_relays_output_to_web()
    {
        var fake = new FakeTerminalSession(seed: "CURRENT-SCREEN");
        var sent = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Action<string> send = s => sent.Enqueue(s);

        Sys.ActorOf(Props.Create<RemoteSessionActor>("k", fake, send));

        // First frame is the current-screen snapshot (raw ANSI, here the seed).
        AwaitAssert(() =>
        {
            Assert.True(sent.TryPeek(out var snapshot));
            Assert.Contains("\"snapshot\"", snapshot);
            Assert.Contains("CURRENT-SCREEN", snapshot);
        });

        // Subsequent PTY output is relayed as output frames.
        fake.RaiseOutput("hello-world");
        AwaitAssert(() =>
        {
            var frames = sent.ToArray();
            Assert.Contains(frames, f => f.Contains("\"output\"") && f.Contains("hello-world"));
        });
    }

    [Fact]
    public void Session_injects_web_input_into_the_terminal()
    {
        var fake = new FakeTerminalSession();
        Action<string> noop = _ => { };
        var actor = Sys.ActorOf(Props.Create(() => new RemoteSessionActor("k", fake, noop)));

        actor.Tell(new RemoteInputText("ls\r"));
        actor.Tell(new RemoteControlKey("ctrlc"));

        AwaitAssert(() =>
        {
            Assert.Contains("ls\r", fake.Writes);
            Assert.Contains(TerminalControl.Interrupt, fake.Controls);
        });
    }

    [Fact]
    public void Hub_enforces_the_connection_cap()
    {
        var hub = Sys.ActorOf(Props.Create(() => new RemoteHubActor(maxConnections: 1)));
        var probe = CreateTestProbe();

        hub.Tell(new CreateRemoteSession("a", new FakeTerminalSession(), _ => { }), probe.Ref);
        probe.ExpectMsg<RemoteSessionCreated>();

        // Second connection over the cap is refused.
        hub.Tell(new CreateRemoteSession("b", new FakeTerminalSession(), _ => { }), probe.Ref);
        probe.ExpectMsg<RemoteSessionRejected>();

        hub.Tell(new QueryRemoteStatus(), probe.Ref);
        var status = probe.ExpectMsg<RemoteStatusReply>();
        Assert.Equal(1, status.Active);
        Assert.Equal(1, status.Max);
    }

    [Fact]
    public void Hub_frees_the_cap_when_a_session_closes()
    {
        var hub = Sys.ActorOf(Props.Create(() => new RemoteHubActor(maxConnections: 1)));
        var probe = CreateTestProbe();

        hub.Tell(new CreateRemoteSession("a", new FakeTerminalSession(), _ => { }), probe.Ref);
        probe.ExpectMsg<RemoteSessionCreated>();

        hub.Tell(new CloseRemoteSession("a"));

        // Cap freed → a new session is accepted.
        AwaitAssert(() =>
        {
            hub.Tell(new CreateRemoteSession("b", new FakeTerminalSession(), _ => { }), probe.Ref);
            probe.ExpectMsg<RemoteSessionCreated>(TimeSpan.FromMilliseconds(500));
        });
    }
}
