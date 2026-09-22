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
        if (model.Step != ConfigStep.Connection) failures.Add($"ended on step {model.Step}, expected Connection");
        if (model.SelectedKey != "provider") failures.Add($"selection is '{model.SelectedKey}', expected 'provider'");

        // Step navigation and the picker are checked without the UI on purpose.
        // Arriving at the Model step starts an asynchronous listing, and while it
        // is in flight the screen ignores keys — so a scripted key sequence that
        // walked the steps would race the request and fail at random in CI rather
        // than when something is actually broken.
        CheckStepNavigation(failures);
        await CheckModelPickerAsync(failures);
        await CheckReasoningPickerAsync(failures);

        // The chat window boots the same host with its own page; it is checked
        // the same way, keys only, no turn submitted.
        failures.AddRange(await ChatTuiApp.SelfTestAsync());

        if (failures.Count > 0)
        {
            Console.Error.WriteLine("agent-one tui selftest: FAILED");
            foreach (var failure in failures) Console.Error.WriteLine("  - " + failure);
            return 1;
        }

        Console.WriteLine("agent-one tui selftest: ok (render + step navigation + key routing + model picker)");
        return 0;
    }

    private static void CheckStepNavigation(List<string> failures)
    {
        var walker = new ConfigTuiModel(new AgentConfig());

        if (walker.Step != ConfigStep.Connection) failures.Add("did not start on the Connection step");

        walker.JumpToStep(ConfigStep.Options);
        if (walker.Step != ConfigStep.Options) failures.Add("could not reach the Options step");
        if (walker.SelectedKey != ConfigTuiModel.StepFields[(int)ConfigStep.Options][0])
            failures.Add($"Options opened on '{walker.SelectedKey}', expected '{ConfigTuiModel.StepFields[(int)ConfigStep.Options][0]}'");

        walker.JumpToStep(ConfigStep.Connection);
        if (walker.Step != ConfigStep.Connection) failures.Add("could not get back to the Connection step");
    }

    /// <summary>
    /// The Reasoning step's model row: Enter asks the endpoint, the list overlays
    /// the step, a pick lands in reasoningModel, and Esc closes the list without
    /// leaving the step.
    /// </summary>
    private static async Task CheckReasoningPickerAsync(List<string> failures)
    {
        var model = new ConfigTuiModel(new AgentConfig());
        model.JumpToStep(ConfigStep.Reasoning);
        model.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.End, false, false, false));     // reasoningModel row

        if (model.SelectedKey != ConfigTuiModel.ReasoningModelField)
            failures.Add($"End on the Reasoning step selected '{model.SelectedKey}'");

        if (model.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)) != TuiEffect.FetchModels)
            failures.Add("Enter on reasoningModel did not request a listing");

        model.CompleteModelFetch(await model.ModelCatalog(model.ProbeTarget, CancellationToken.None));
        if (!model.Picking) { failures.Add("the reasoning listing did not open the picker"); return; }

        model.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, false));
        model.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        if (model.Value(ConfigTuiModel.ReasoningModelField) != "echo")
            failures.Add($"picking left reasoningModel as '{model.Value(ConfigTuiModel.ReasoningModelField)}', expected 'echo'");

        model.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false));
        if (model.Picking) failures.Add("Esc did not close the reasoning list");
        if (model.Step != ConfigStep.Reasoning) failures.Add("Esc on the list left the Reasoning step");
    }

    private static async Task CheckModelPickerAsync(List<string> failures)
    {
        // Happy path, offline: arriving at the Model step asks, and the echo
        // provider lists itself.
        var picker = new ConfigTuiModel(new AgentConfig());
        if (picker.JumpToStep(ConfigStep.Model) != TuiEffect.FetchModels)
            failures.Add("arriving at the Model step did not request a listing");

        picker.CompleteModelFetch(await picker.ModelCatalog(picker.Config, CancellationToken.None));

        if (!picker.Picking) failures.Add("a successful listing did not open the picker");
        else
        {
            picker.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, false));
            picker.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
            if (picker.Value("model") != "echo") failures.Add($"picking left model as '{picker.Value("model")}', expected 'echo'");
            if (!picker.Picking) failures.Add("the list should stay up after a choice");
        }

        // Failure path: a port nothing listens on refuses immediately, so this
        // stays fast and offline while still walking the real HTTP client.
        var dead = new AgentConfig();
        dead.TrySet("provider", "openai", out _);
        dead.TrySet("baseUrl", "http://127.0.0.1:1/v1", out _);
        dead.TrySet("timeoutSeconds", "5", out _);

        var unreachable = new ConfigTuiModel(dead);
        unreachable.JumpToStep(ConfigStep.Model);
        unreachable.CompleteModelFetch(await unreachable.ModelCatalog(dead, CancellationToken.None));

        if (unreachable.Picking) failures.Add("an unreachable endpoint still opened the picker");
        if (!unreachable.Status.StartsWith('✗')) failures.Add("an unreachable endpoint was not reported as a failure");
        if (unreachable.Busy) failures.Add("the screen stayed busy after a failed listing");
    }
}
