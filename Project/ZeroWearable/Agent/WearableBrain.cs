using Akka.Actor;
using Agent.Common.Actors;
using Agent.Common.Llm;
using Agent.Common.Llm.Tools;
using Agent.Common.Wearable;
using ZeroWearable.Chat;

namespace ZeroWearable.Agent;

/// <summary>
/// Whatever answers the watch <i>outside</i> the actor system — today only an agent CLI as a
/// child process (<see cref="CliBrain"/>). AgentZero's own agent no longer implements this:
/// since M0032 it is an actor subtree (<see cref="Agent.Common.Wearable.Actors.WearableAgentActor"/>)
/// that <see cref="Actors.ChatActor"/> talks to with messages, so a running tool call never
/// blocks the device's mailbox and progress can flow back per phase.
/// </summary>
public interface IWearableBrain : IAsyncDisposable
{
    /// <summary>
    /// What the device prints in its status line. The firmware keeps 24 bytes for this,
    /// so implementations trim vendor prefixes rather than let the name be cut.
    /// </summary>
    string Name { get; }

    /// <summary>One line describing the brain for the host's startup banner.</summary>
    string Status { get; }

    /// <param name="replyLanguage">
    /// The watch's output-language setting, so what is shown and what is spoken are the
    /// same words. Null/"auto" leaves the choice to the script of the question.
    /// </param>
    Task<string> AskAsync(string prompt, string session, string? replyLanguage, CancellationToken ct);

    /// <summary>Forget a conversation — the device's "new conversation" button.</summary>
    void Reset(string session);
}

/// <summary>
/// The watch talking to an agent CLI (claude, netclaw, …) as a child process — the path
/// the reference host shipped, kept because those CLIs are whole agents with their own
/// toolchains. Sessions are theirs to keep: the session name is passed through and a new
/// conversation is simply a new name.
/// </summary>
public sealed class CliBrain : IWearableBrain
{
    private readonly CliProvider _provider;
    private readonly string _replyStyle;

    public CliBrain(string name, ProviderConfig config, string replyStyle, Action<string, string> log)
    {
        _provider = new CliProvider(name, config, log);
        Name = name;
        Status = config.Description ?? name;
        // A CLI that echoes instructions back (the loopback provider) would have the watch
        // read the system prompt aloud.
        _replyStyle = config.UseReplyStyle ? replyStyle : "";
    }

    public string Name { get; }
    public string Status { get; }

    public Task<string> AskAsync(string prompt, string session, string? replyLanguage, CancellationToken ct)
    {
        var styled = _replyStyle.Length > 0 ? _replyStyle + "\n\n" + prompt : prompt;
        return _provider.AskAsync(styled, session, ct);
    }

    /// <summary>Nothing to forget here — the CLI keys its own history off the session name.</summary>
    public void Reset(string session) { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Everything the actor-backed brain needs, decided from Settings → LLM before any actor
/// exists: a name for the device's status line, a status line for the banner, the loop
/// bindings (built once the tool actors exist, since the toolbelt is an adapter over them)
/// and, for the on-device brain, the model handle whose life is the agent actor's.
/// </summary>
public sealed record WearableAgentPlan(
    string Name,
    string Status,
    Func<IActorRef, IActorRef, AgentLoopBindings> Bindings,
    IAsyncDisposable? Owned);

/// <summary>
/// Builds the configured brain out of AgentZero's own settings. Returns null with a reason
/// when the choice cannot be honoured, so the host can start and tell the device it has no
/// agent rather than dying at the first question.
/// </summary>
public static class WearableBrainFactory
{
    /// <summary>The two AgentZero brains run inside the actor system; the CLI brain runs beside it.</summary>
    public static bool UsesAgentActor(WearableSettings settings)
        => !string.Equals(settings.Brain, WearableBrainNames.Cli, StringComparison.OrdinalIgnoreCase);

    public static IWearableBrain CreateCliBrain(WearableSettings settings, Action<string, string> log, out string status)
    {
        var name = CliProviderCatalog.Normalize(settings.CliProvider);
        var config = CliProviderCatalog.Resolve(name);
        status = $"cli:{name} — {config.Description}";
        return new CliBrain(name, config, settings.ReplyStyle, log);
    }

    /// <summary>
    /// The same <see cref="IAgentLoop"/> the AgentBot window drives, with the same GBNF tool
    /// envelope and the same LLM settings — Local (on-device GGUF) or External (Webnori /
    /// LM Studio / OpenAI / Ollama) exactly as Settings → LLM says. Nothing about the model
    /// is configured twice.
    /// </summary>
    public static WearableAgentPlan? CreateAgentPlan(WearableSettings settings, Action<string, string> log, out string status)
    {
        if (string.Equals(settings.Brain, WearableBrainNames.AgentLocal, StringComparison.OrdinalIgnoreCase))
        {
            var llm = settings.LocalModelId;
            var entry = string.IsNullOrWhiteSpace(llm)
                ? LlmModelCatalog.Default
                : LlmModelCatalog.FindById(llm);
            if (!LlmModelLocator.IsAvailable(entry))
            {
                status = $"local model '{entry.Id}' is not downloaded — " +
                         "Settings → LLM → Download, or pick the External brain";
                return null;
            }

            var runtime = LlmSettingsStore.Load();
            var options = LoopOptions(Math.Max(256, runtime.AgentToolLoopMaxTokens), runtime.Temperature);
            var template = entry.ChatFamily.Equals("llama31", StringComparison.OrdinalIgnoreCase)
                ? ChatTemplates.Llama31
                : ChatTemplates.Gemma;
            var modelPath = LlmModelLocator.ResolveExistingOrTarget(entry);

            // The GGUF is loaded lazily but ONCE, and shared by every session's loop: the
            // first question pays the load, a host with no watch in range never pays it,
            // and a second conversation does not put a second copy of the model in VRAM.
            // LocalAgentLoop takes its own LLamaContext off these weights, which is the
            // per-conversation state.
            var modelLock = new object();
            LlamaSharpLocalLlm? loaded = null;
            var holder = new LocalModelHolder(() => loaded);

            status = $"agent:{Short(entry.Id)} on-device ({entry.FileName})";
            return new WearableAgentPlan(
                Name: "agent:" + Short(entry.Id),
                Status: status,
                Bindings: (files, web) => new AgentLoopBindings(
                    ToolbeltFactory: () => new WearableToolbelt(files, web),
                    OptionsFactory: () => options,
                    // `opts` (not `options`) carries the actor's progress callbacks.
                    AgentLoopFactory: (opts, host) =>
                    {
                        lock (modelLock)
                        {
                            loaded ??= LlamaSharpLocalLlm
                                .CreateAsync(runtime.ToOptions(modelPath))
                                .GetAwaiter().GetResult();
                            return new LocalAgentLoop(loaded, host, opts, template);
                        }
                    }),
                Owned: holder);
        }
        else
        {
            var runtime = LlmSettingsStore.Load();
            var provider = runtime.CreateExternalProvider();
            var model = runtime.ResolveExternalModel();
            if (provider is null || string.IsNullOrEmpty(model))
            {
                status = $"external provider '{runtime.External.Provider}' has no model selected — " +
                         "Settings → LLM";
                return null;
            }

            var options = LoopOptions(Math.Max(256, runtime.External.MaxTokens), runtime.Temperature);
            status = $"agent:{Short(model)} at {provider.ProviderName}";
            return new WearableAgentPlan(
                Name: "agent:" + Short(model),
                Status: status,
                Bindings: (files, web) => new AgentLoopBindings(
                    ToolbeltFactory: () => new WearableToolbelt(files, web),
                    OptionsFactory: () => options,
                    AgentLoopFactory: (opts, host) => new ExternalAgentLoop(provider, model, host, opts)),
                Owned: null);
        }
    }

    /// <summary>
    /// The watch asks one question and wants one answer, so the loop is kept short: a
    /// runaway relay would be minutes of silence on a screen with no scrollback. Eight
    /// turns is list → read → done with room for one web search → open → done.
    /// </summary>
    private static AgentLoopOptions LoopOptions(int maxTokensPerTurn, float temperature) => new()
    {
        MaxIterations = 8,
        MaxTokensPerTurn = maxTokensPerTurn,
        Temperature = temperature,
    };

    /// <summary>Drops the vendor prefix ("google/gemma-4-e4b" → "gemma-4-e4b") — the device
    /// keeps 24 bytes for the status line and the name is the informative half.</summary>
    private static string Short(string model)
    {
        var slash = model.LastIndexOf('/');
        return slash >= 0 ? model[(slash + 1)..] : model;
    }

    /// <summary>
    /// Hands the agent actor's disposal a handle on a model that may never have been loaded —
    /// the closure decides at dispose time whether there is anything to unload.
    /// </summary>
    private sealed class LocalModelHolder(Func<LlamaSharpLocalLlm?> current) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => current() is { } llm ? llm.DisposeAsync() : ValueTask.CompletedTask;
    }
}
