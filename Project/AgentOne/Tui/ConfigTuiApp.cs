using AgentOne.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Termina.Hosting;
using Termina.Input;

namespace AgentOne.Tui;

/// <summary>
/// Hosts the settings screen. Everything here is wiring; the rules are in
/// <see cref="ConfigTuiModel"/> and the pixels in <see cref="ConfigTuiPage"/>.
/// </summary>
public static class ConfigTuiApp
{
    /// <param name="scripted">
    /// Optional keystroke script. When supplied the host reads from it instead of
    /// the terminal, which is how the TUI is smoke-tested without a console.
    /// </param>
    public static async Task<int> RunAsync(ConfigTuiModel? model = null, VirtualInputSource? scripted = null)
    {
        // A full-screen UI in a pipe renders escape sequences into someone's log
        // file and then waits forever for a key that cannot arrive.
        if (scripted is null && (Console.IsInputRedirected || Console.IsOutputRedirected))
        {
            Console.Error.WriteLine("agent-one config tui: needs an interactive terminal (stdin/stdout are redirected).");
            Console.Error.WriteLine("agent-one config tui: use `agent-one config set <key> <value>` in scripts.");
            return 2;
        }

        var builder = Host.CreateApplicationBuilder();

        // Hosting's lifetime logger writes "Application is shutting down..." over
        // the alternate screen buffer on the way out. Nothing here wants a logger.
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(model ?? ConfigTuiModel.Load());
        builder.Services.AddTransient<ConfigTuiViewModel>();

        builder.Services.AddTermina("/config", termina =>
            termina.RegisterRoute<ConfigTuiPage, ConfigTuiViewModel>("/config"));

        if (scripted is not null) builder.Services.AddTerminaVirtualInput(scripted);

        await builder.Build().RunAsync();
        return 0;
    }

    /// <summary>
    /// Runs the real screen against a scripted key sequence and checks where it
    /// ended up. This is how a published binary proves its TUI works on a machine
    /// with no terminal — CI, a container, a release smoke step — since a Native
    /// AOT failure shows up when the host starts, not when it links.
    /// </summary>
    public static async Task<int> SelfTestAsync()
    {
        var model = new ConfigTuiModel(new AgentConfig());

        var scripted = new VirtualInputSource();
        scripted.EnqueueKey(ConsoleKey.DownArrow);   // provider -> baseUrl
        scripted.EnqueueKey(ConsoleKey.UpArrow);     // back to provider
        scripted.EnqueueKey(ConsoleKey.RightArrow);  // cycle echo -> openai
        scripted.EnqueueKey(ConsoleKey.Q);           // dirty: arms the discard prompt
        scripted.EnqueueKey(ConsoleKey.Q);           // confirm: quit
        scripted.Complete();

        await RunAsync(model, scripted);

        var failures = new List<string>();
        if (model.Value("provider") != "openai") failures.Add($"provider is '{model.Value("provider")}', expected 'openai'");
        if (!model.Dirty) failures.Add("model should be dirty after cycling provider");
        if (model.SelectedKey != "provider") failures.Add($"selection is '{model.SelectedKey}', expected 'provider'");

        if (failures.Count > 0)
        {
            Console.Error.WriteLine("agent-one tui selftest: FAILED");
            foreach (var failure in failures) Console.Error.WriteLine("  - " + failure);
            return 1;
        }

        Console.WriteLine("agent-one tui selftest: ok (render + key routing + state)");
        return 0;
    }
}
