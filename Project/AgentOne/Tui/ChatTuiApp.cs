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
    /// Boots the real window against scripted keys and checks what it left
    /// behind: the line editor, the mode toggle, and — after one echo turn —
    /// that PageUp left the reader above the live end.
    ///
    /// Every key is queued before the window starts, on purpose. A key pushed
    /// into an <em>idle</em> virtual queue is forwarded by a continuation that,
    /// under Native AOT, runs only once the loop wakes for something else
    /// (2–9 s measured; real console keys arrive in under 100 ms, so users never
    /// see it). Keeping the queue non-empty sidesteps that entirely. It works
    /// because the echo turn completes inside the Enter keystroke — the provider
    /// answers synchronously — so PageUp finds the answer already in the buffer.
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
        scripted.EnqueueKey(ConsoleKey.Tab, true, false, false);  // Shift+Tab: flips smart only when a key is stored
        scripted.EnqueueKey(ConsoleKey.Escape);               // clears the line

        // Back to basic if the toggle took, so the turn calls no decision engine.
        if (session.SmartAvailable) scripted.EnqueueKey(ConsoleKey.Tab, true, false, false);

        // Enough text (≈30 wrapped lines, echoed back once more) to overflow the 24-row headless window.
        scripted.EnqueueString(string.Join(" ", Enumerable.Range(1, 300).Select(i => $"word{i}")));
        scripted.EnqueueKey(ConsoleKey.Enter);
        scripted.EnqueueKey(ConsoleKey.PageUp);
        scripted.EnqueueScroll(10, 5, up: false);             // one wheel tick back down: a few lines, not a page
        scripted.EnqueueKey(ConsoleKey.Escape);               // arms quit
        scripted.EnqueueKey(ConsoleKey.Escape);               // quits
        scripted.Complete();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(session);
        builder.Services.AddSingleton(model);
        builder.Services.AddTermina("/chat", t => t.RegisterRoute<ChatTuiPage, ChatTuiViewModel>("/chat"));
        builder.Services.AddTerminaVirtualInput(scripted);
        await builder.Build().RunAsync();

        var failures = new List<string>();
        if (model.Input.Length != 0) failures.Add($"chat input should be empty after Esc, was '{model.Input}'");
        if (model.Smart) failures.Add("chat should be back in basic mode");
        if (model.Turns != 1 || model.Busy) failures.Add("the echo turn did not finish");
        // PageUp moved a page (20 lines in the 24-row headless window); the wheel
        // tick brought a few back, so the offset must be above zero and below a page.
        if (!model.ScrolledUp || model.ScrollOffset <= 0)
            failures.Add($"PageUp should leave the transcript above the live end (offset {model.ScrollOffset})");
        else if (model.ScrollOffset >= 20)
            failures.Add($"the wheel tick should have scrolled a few lines back down (offset {model.ScrollOffset})");
        if (!model.QuitArmed) failures.Add("the last Esc should have quit through the armed path");
        return failures;
    }
}
