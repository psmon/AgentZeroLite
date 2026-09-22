using AgentOne.Agent;
using AgentOne.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Termina.Hosting;
using Termina.Input;

namespace AgentOne.Tui;

/// <summary>Hosts the chat window. Wiring only; the conversation is <see cref="ChatSession"/>.</summary>
public static class ChatTuiApp
{
    public static async Task<int> RunAsync(ChatSession session, VirtualInputSource? scripted = null)
    {
        if (scripted is null && (Console.IsInputRedirected || Console.IsOutputRedirected))
        {
            Console.Error.WriteLine("agent-one chat: the chat window needs an interactive terminal; use --plain when piping.");
            return 2;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(session);
        builder.Services.AddSingleton(new ChatTuiModel(session.Smart, session.SmartAvailable));
        builder.Services.AddTransient<ChatTuiViewModel>();

        builder.Services.AddTermina("/chat", termina =>
            termina.RegisterRoute<ChatTuiPage, ChatTuiViewModel>("/chat"));

        if (scripted is not null) builder.Services.AddTerminaVirtualInput(scripted);

        await builder.Build().RunAsync();
        return 0;
    }

    /// <summary>
    /// Boots the real window against scripted keys and checks the input state it
    /// left behind. No turn is submitted: a turn is asynchronous and a scripted
    /// key racing it would fail at random. The turn pipeline has its own tests.
    /// </summary>
    public static async Task<List<string>> SelfTestAsync()
    {
        var config = new AgentConfig();                // echo provider, no keys needed
        using var session = new ChatSession(config, Directory.GetCurrentDirectory(), streaming: false);
        var model = new ChatTuiModel(session.Smart, session.SmartAvailable);

        var scripted = new VirtualInputSource();
        scripted.EnqueueString("hello");
        scripted.EnqueueKey(ConsoleKey.LeftArrow);
        scripted.EnqueueKey(ConsoleKey.Backspace);            // "helo"
        scripted.EnqueueKey(ConsoleKey.Tab, true, false, false);  // Shift+Tab: no key, must refuse
        scripted.EnqueueKey(ConsoleKey.Escape);               // clears the line
        scripted.EnqueueKey(ConsoleKey.Escape);               // arms quit
        scripted.EnqueueKey(ConsoleKey.Escape);               // quits
        scripted.Complete();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(session);
        builder.Services.AddSingleton(model);
        builder.Services.AddTransient<ChatTuiViewModel>();
        builder.Services.AddTermina("/chat", t => t.RegisterRoute<ChatTuiPage, ChatTuiViewModel>("/chat"));
        builder.Services.AddTerminaVirtualInput(scripted);
        await builder.Build().RunAsync();

        var failures = new List<string>();
        if (model.Input.Length != 0) failures.Add($"chat input should be empty after Esc, was '{model.Input}'");
        if (model.Smart) failures.Add("chat switched to smart with no TypeSafe key");
        if (!model.QuitArmed) failures.Add("the last Esc should have quit through the armed path");
        return failures;
    }
}
