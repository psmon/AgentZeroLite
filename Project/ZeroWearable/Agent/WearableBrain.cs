using Agent.Common.Llm;
using Agent.Common.Llm.Tools;
using Agent.Common.Wearable;
using ZeroWearable.Chat;

namespace ZeroWearable.Agent;

/// <summary>
/// Whatever answers the watch. Two shapes exist — AgentZero's own agent loop
/// (<see cref="AgentLoopBrain"/>) and an agent CLI as a child process
/// (<see cref="CliBrain"/>) — and <see cref="ChatActor"/> does not care which it holds.
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
/// The watch talking to <b>AgentZero's own agent</b>: the same
/// <see cref="IAgentLoop"/> the AgentBot window drives, with the same GBNF tool envelope
/// and the same LLM settings — Local (on-device GGUF) or External (Webnori / LM Studio /
/// OpenAI / Ollama) exactly as Settings → LLM says. Nothing about the model is configured
/// twice.
///
/// <para>One loop per session, because the loop <i>is</i> the conversation: it holds the
/// KV cache (Local) or the replayed message list (External). A device that starts a new
/// conversation gets its loop disposed, which is also how the history is dropped.</para>
///
/// <para>The loop's tool surface is <see cref="WearableToolbelt"/> — files only, and only
/// inside the configured root.</para>
/// </summary>
public sealed class AgentLoopBrain : IWearableBrain
{
    private readonly Func<IAgentToolbelt, IAgentLoop?> _factory;
    private readonly IAgentToolbelt _toolbelt;
    private readonly Action<string, string> _log;
    private readonly Dictionary<string, IAgentLoop> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly object _lock = new();

    /// <param name="owned">
    /// Something the factory closes over that outlives the individual loops — the loaded
    /// GGUF, for the on-device brain. Disposed with the brain, not with a session.
    /// </param>
    public AgentLoopBrain(string name, string status, IAgentToolbelt toolbelt,
        Func<IAgentToolbelt, IAgentLoop?> factory, Action<string, string> log,
        IAsyncDisposable? owned = null)
    {
        Name = name;
        Status = status;
        _toolbelt = toolbelt;
        _factory = factory;
        _log = log;
        _owned = owned;
    }

    private readonly IAsyncDisposable? _owned;

    public string Name { get; }
    public string Status { get; }

    public async Task<string> AskAsync(string prompt, string session, string? replyLanguage,
        CancellationToken ct)
    {
        // One question at a time. A local model serves one prompt at a time anyway, and
        // serialising here keeps two devices from interleaving tool calls.
        await _slot.WaitAsync(ct);
        try
        {
            var loop = Loop(session);
            var run = await loop.RunAsync(Frame(prompt, replyLanguage), ct);

            var answer = (run.FinalMessage ?? "").Trim();
            if (answer.Length == 0)
            {
                // A loop that stopped with nothing to say is still an answer to report:
                // say what happened rather than showing the watch an empty bubble.
                answer = run.FailureReason is { Length: > 0 } reason
                    ? $"I could not finish that ({reason})."
                    : "I have no answer for that.";
            }
            if (!run.TerminatedCleanly)
                _log("warn", $"agent loop ended unclean after {run.TurnCount} turn(s): {run.FailureReason}");
            return answer;
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// The watch's constraints, carried per request rather than baked into the shared
    /// system prompt: <see cref="AgentToolGrammar.SystemPrompt"/> belongs to the whole app
    /// and must not grow a wearable clause. A small model also answers a Korean question in
    /// English unless the language is named — asking it to "match the user" did not work.
    /// </summary>
    private static string Frame(string prompt, string? replyLanguage)
    {
        var language = LanguageName(replyLanguage, prompt);
        return $"""
                [The answer is shown on a small round smartwatch screen and read aloud.
                 Answer in {language}, in at most two short sentences, plain text only —
                 no markdown, no lists, no code blocks, no URLs.]

                {prompt}
                """;
    }

    private static string LanguageName(string? requested, string prompt)
    {
        var code = (requested ?? "").Trim().ToLowerInvariant();
        if (code.Length == 0 || code == "auto" || code == "na")
            code = prompt.Any(c => c >= 0xAC00 && c <= 0xD7A3) ? "ko" : "en";

        return code switch
        {
            "ko" or "ko-kr" => "Korean",
            "en" or "en-us" or "en-gb" => "English",
            "ja" => "Japanese",
            "zh" => "Chinese",
            _ => code,
        };
    }

    private IAgentLoop Loop(string session)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(session, out var existing)) return existing;
            var created = _factory(_toolbelt)
                ?? throw new InvalidOperationException(
                    "no agent loop could be built — check Settings → LLM (model loaded? provider reachable?)");
            _sessions[session] = created;
            _log("info", $"agent loop opened for session {session}");
            return created;
        }
    }

    public void Reset(string session)
    {
        IAgentLoop? loop;
        lock (_lock)
        {
            if (!_sessions.Remove(session, out loop)) return;
        }
        _ = DisposeQuietly(loop);
    }

    public async ValueTask DisposeAsync()
    {
        List<IAgentLoop> loops;
        lock (_lock)
        {
            loops = _sessions.Values.ToList();
            _sessions.Clear();
        }
        foreach (var loop in loops) await DisposeQuietly(loop);
        if (_owned is not null)
        {
            try
            {
                await _owned.DisposeAsync();
            }
            catch (Exception ex)
            {
                _log("warn", $"unloading the model threw: {ex.Message}");
            }
        }
        _slot.Dispose();
    }

    private async Task DisposeQuietly(IAgentLoop loop)
    {
        try
        {
            await loop.DisposeAsync();
        }
        catch (Exception ex)
        {
            _log("warn", $"disposing an agent loop threw: {ex.Message}");
        }
    }
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
/// Builds the configured brain out of AgentZero's own settings. Returns null with a reason
/// when the choice cannot be honoured, so the host can start and tell the device it has no
/// agent rather than dying at the first question.
/// </summary>
public static class WearableBrainFactory
{
    public static IWearableBrain? Create(WearableSettings settings, Action<string, string> log,
        out string status)
    {
        switch (settings.Brain)
        {
            case WearableBrainNames.Cli:
            {
                var name = CliProviderCatalog.Normalize(settings.CliProvider);
                var config = CliProviderCatalog.Resolve(name);
                status = $"cli:{name} — {config.Description}";
                return new CliBrain(name, config, settings.ReplyStyle, log);
            }

            case WearableBrainNames.AgentLocal:
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

                var toolbelt = new WearableToolbelt(settings.WorkspaceRoot, log);
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
                return new AgentLoopBrain(
                    name: "agent:" + Short(entry.Id),
                    status: status,
                    toolbelt,
                    factory: host =>
                    {
                        lock (modelLock)
                        {
                            loaded ??= LlamaSharpLocalLlm
                                .CreateAsync(runtime.ToOptions(modelPath))
                                .GetAwaiter().GetResult();
                            return new LocalAgentLoop(loaded, host, options, template);
                        }
                    },
                    log,
                    owned: holder);
            }

            default:
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

                var toolbelt = new WearableToolbelt(settings.WorkspaceRoot, log);
                var options = LoopOptions(Math.Max(256, runtime.External.MaxTokens), runtime.Temperature);

                status = $"agent:{Short(model)} at {provider.ProviderName}";
                return new AgentLoopBrain(
                    name: "agent:" + Short(model),
                    status: status,
                    toolbelt,
                    factory: host => new ExternalAgentLoop(provider, model, host, options),
                    log);
            }
        }
    }

    /// <summary>
    /// The watch asks one question and wants one answer, so the loop is kept short: a
    /// runaway relay would be minutes of silence on a screen with no scrollback.
    /// </summary>
    private static AgentLoopOptions LoopOptions(int maxTokensPerTurn, float temperature) => new()
    {
        MaxIterations = 6,
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
    /// Hands the brain's disposal a handle on a model that may never have been loaded —
    /// the closure decides at dispose time whether there is anything to unload.
    /// </summary>
    private sealed class LocalModelHolder(Func<LlamaSharpLocalLlm?> current) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => current() is { } llm ? llm.DisposeAsync() : ValueTask.CompletedTask;
    }
}
