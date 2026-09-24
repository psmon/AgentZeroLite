using Agent.Common;
using Agent.Common.Actors;
using Agent.Common.Llm;
using Agent.Common.Llm.Tools;
using Agent.Common.Module;

namespace AgentZeroAvalonia.Services;

/// <summary>
/// What the <c>AgentBotActor</c> needs to spawn its <c>AgentLoopActor</c> (M0037): a port of
/// the WPF <c>AgentBotWindow.EnsureAgentLoopWiring</c> bindings. Backend-agnostic: External
/// (OpenAI-compatible REST) on every OS; Local (LLamaSharp) only where <see cref="LlmService"/>
/// has a loaded model, which is Windows.
/// </summary>
internal static class AgentLoopWiring
{
    public static AgentLoopBindings Build(Func<IReadOnlyList<ICliGroupInfo>> groups, Func<string?> activeDirectory)
    {
        string? WorkspaceRoot()
        {
            var active = activeDirectory();
            if (!string.IsNullOrWhiteSpace(active) && Directory.Exists(active)) return active;
            foreach (var g in groups())
                if (!string.IsNullOrWhiteSpace(g.DirectoryPath) && Directory.Exists(g.DirectoryPath)) return g.DirectoryPath;
            return null;
        }

        return new AgentLoopBindings(
            ToolbeltFactory: () => new WorkspaceToolHost(groups, WorkspaceRoot),
            OptionsFactory: () =>
            {
                var settings = LlmSettingsStore.Load();
                // The turn budget belongs to the loop, not to a backend, so it is read
                // from the persisted settings on both paths — LlmService.CurrentSettings
                // mirrors the loaded model and says nothing about how long a chain may run.
                var maxTurns = settings.ResolveAgentLoopMaxTurns();
                if (settings.ActiveBackend == LlmActiveBackend.Local)
                {
                    var s = LlmService.CurrentSettings;
                    var entry = LlmModelCatalog.FindById(s.ModelId);
                    var isLlama31 = entry.ChatFamily.Equals("llama31", StringComparison.OrdinalIgnoreCase);
                    var isVulkan = s.Backend == LocalLlmBackend.Vulkan;
                    var temp = (isLlama31 && isVulkan) ? 0.0f : s.Temperature;
                    var cap = Math.Max(256, s.AgentToolLoopMaxTokens);
                    AppLogger.Log($"[AIMODE] options: backend=Local maxTokens={cap} maxTurns={maxTurns} temp={temp:0.00} family={entry.ChatFamily}");
                    return new AgentLoopOptions { MaxTokensPerTurn = cap, MaxIterations = maxTurns, Temperature = temp };
                }
                else
                {
                    var cap = Math.Max(256, settings.External.MaxTokens);
                    AppLogger.Log($"[AIMODE] options: backend=External provider={settings.External.Provider} model={settings.ResolveExternalModel()} maxTokens={cap} maxTurns={maxTurns} temp={settings.Temperature:0.00}");
                    return new AgentLoopOptions { MaxTokensPerTurn = cap, MaxIterations = maxTurns, Temperature = settings.Temperature };
                }
            },
            AgentLoopFactory: (opts, host) =>
            {
                var settings = LlmSettingsStore.Load();
                if (settings.ActiveBackend == LlmActiveBackend.Local)
                {
                    if (LlmService.Llm is not LlamaSharpLocalLlm llm) return null;
                    var entry = LlmModelCatalog.FindById(LlmService.CurrentSettings.ModelId);
                    var template = entry.ChatFamily.Equals("llama31", StringComparison.OrdinalIgnoreCase)
                        ? ChatTemplates.Llama31
                        : ChatTemplates.Gemma;
                    return new LocalAgentLoop(llm, host, opts, template);
                }
                var provider = settings.CreateExternalProvider();
                var model = settings.ResolveExternalModel();
                if (provider is null || string.IsNullOrEmpty(model)) return null;
                return new ExternalAgentLoop(provider, model, host, opts);
            });
    }

    /// <summary>"Ollama · llama3" or the local model's display name — for the status line.</summary>
    public static string ActiveModelLabel()
    {
        try
        {
            var s = LlmSettingsStore.Load();
            return s.ActiveBackend == LlmActiveBackend.Local
                ? LlmModelCatalog.FindById(LlmService.CurrentSettings.ModelId).DisplayName
                : $"{s.External.Provider} · {s.ResolveExternalModel()}";
        }
        catch { return "LLM"; }
    }

    /// <summary>Why AI mode cannot run right now, or null when it can.</summary>
    public static string? Unavailability()
    {
        LlmRuntimeSettings s;
        try { s = LlmSettingsStore.Load(); } catch (Exception ex) { return "LLM settings could not be read: " + ex.Message; }
        if (s.ActiveBackend == LlmActiveBackend.Local)
        {
            if (!OperatingSystem.IsWindows())
                return "The local LLM (LLamaSharp) runs on Windows only. Open Settings → LLM and switch to External.";
            if (LlmService.Llm is null)
                return "AI mode needs a loaded local model. Open Settings → LLM and click Load, or switch to External.";
            return null;
        }
        return LlmGateway.IsActiveAvailable()
            ? null
            : $"AI mode (External / {s.External.Provider}) is not configured. Open Settings → LLM → External and pick a provider, model and (for OpenAI) an API key.";
    }
}
