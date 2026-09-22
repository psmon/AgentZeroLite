using System.Diagnostics;
using AgentOne.Actors;
using AgentOne.Agent;
using AgentOne.Services;
using AgentOne.Tui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Termina.Hosting;
using Termina.Input;

namespace AgentOne.Tests;

/// <summary>
/// The real (headless) window over the actor pair. Measured before the fix:
/// the bot's first callback repainted the window from the bot's own thread
/// and deadlocked against Termina's loop — the turn never came back and no
/// key was read again, which the person saw as "blocked". Now the gateway
/// pumps events on its own thread and the view model posts them to the loop.
/// </summary>
[Collection(AgentOneHomeCollection.Name)]
public sealed class ChatTuiOverActorsTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;

    public ChatTuiOverActorsTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "agent-one-tuiactors-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
        _root = Path.Combine(_home, "ws");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, null);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task TheWindowOverTheGatewayFinishesATurnWithoutAnotherKeyAndStillReadsKeys()
    {
        var config = new AgentConfig();                // echo provider, no keys
        config.TrySet("saveSessions", "false", out _);
        using var session = AgentGateway.Start(config, _root, streaming: true);
        var model = new ChatTuiModel(session.Smart, session.SmartAvailable);

        var scripted = new VirtualInputSource();
        scripted.EnqueueString("hello over actors");
        scripted.EnqueueKey(ConsoleKey.Enter);

        var sw = Stopwatch.StartNew();
        long? doneAt = null;
        var driver = Task.Run(async () =>
        {
            // No key is queued while the turn runs: the answer has to arrive on its own.
            for (var i = 0; i < 300 && doneAt is null; i++)
            {
                await Task.Delay(50);
                if (model.Turns == 1 && !model.Busy) doneAt = sw.ElapsedMilliseconds;
            }
            // And keys still work afterwards: F2, then quit.
            scripted.EnqueueKey(ConsoleKey.F2);
            scripted.EnqueueKey(ConsoleKey.Escape);
            scripted.EnqueueKey(ConsoleKey.Escape);
            scripted.Complete();
        });

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAgentSession>(session);
        builder.Services.AddSingleton(model);
        builder.Services.AddTermina("/chat", t => t.RegisterRoute<ChatTuiPage, ChatTuiViewModel>("/chat"));
        builder.Services.AddTerminaVirtualInput(scripted);
        await builder.Build().RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await driver;

        Assert.NotNull(doneAt);
        Assert.Equal(1, model.Turns);
        Assert.False(model.Busy);
        Assert.True(model.QuitArmed, "the Esc keys after the turn were not read");
    }
}
