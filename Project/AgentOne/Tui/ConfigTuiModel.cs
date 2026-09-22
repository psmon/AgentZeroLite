using AgentOne.Llm;
using AgentOne.Services;

namespace AgentOne.Tui;

/// <summary>What the shell must do after a keystroke the model already absorbed.</summary>
public enum TuiEffect
{
    None,
    Quit,
    /// <summary>Kick off the connection probe; the shell reports back via <see cref="ConfigTuiModel.CompleteTest"/>.</summary>
    RunTest,
    /// <summary>Ask the endpoint what models it has; the shell reports back via <see cref="ConfigTuiModel.CompleteModelFetch"/>.</summary>
    FetchModels
}

/// <summary>
/// The whole settings screen as a state machine, with no Termina and no console
/// in sight. Keeping it here means the key map itself is unit-tested — feed it
/// a <see cref="ConsoleKeyInfo"/>, assert on the state — while the Termina page
/// stays a thin renderer over these properties.
/// </summary>
public sealed class ConfigTuiModel
{
    private Dictionary<string, string> _saved;

    public ConfigTuiModel(AgentConfig config)
    {
        Config = config;
        _saved = Snapshot(config);
        Status = "↑↓ to move · Enter to edit · s to save";
    }

    public static ConfigTuiModel Load()
    {
        var config = ConfigStore.Load(out var warning);
        var model = new ConfigTuiModel(config);
        if (warning.Length > 0) model.Status = warning;
        return model;
    }

    public AgentConfig Config { get; private set; }

    public IReadOnlyList<string> Keys => AgentConfig.Keys;

    public int Selected { get; private set; }

    public string SelectedKey => Keys[Selected];

    public bool Editing { get; private set; }

    public string EditBuffer { get; private set; } = "";

    /// <summary>True while a request is in flight; keys other than quit are ignored.</summary>
    public bool Busy { get; private set; }

    /// <summary>True while the model list is on screen.</summary>
    public bool Picking { get; private set; }

    /// <summary>Model ids offered by the endpoint, plus a final "type it myself" entry.</summary>
    public IReadOnlyList<string> PickOptions { get; private set; } = [];

    public int PickIndex { get; private set; }

    /// <summary>The sentinel last row of the picker — choosing it falls back to free-text entry.</summary>
    public const string PickManualEntry = "· type a model id myself ·";

    public string Status { get; private set; }

    /// <summary>Set when a keystroke changed something the renderer must show.</summary>
    public bool Dirty => Keys.Any(k => _saved[k] != Config.Get(k));

    /// <summary>Armed by the first quit attempt while dirty; a second quit then discards.</summary>
    public bool QuitArmed { get; private set; }

    /// <summary>
    /// The probe, injectable so tests never touch a network. Returns a message to
    /// show; throws nothing — failures come back as text.
    /// </summary>
    public Func<AgentConfig, CancellationToken, Task<string>> ConnectionTest { get; set; } =
        ConfigTuiProbe.DefaultAsync;

    /// <summary>The model listing, injectable for the same reason.</summary>
    public Func<AgentConfig, CancellationToken, Task<ModelCatalogResult>> ModelCatalog { get; set; } =
        ConfigTuiProbe.ListModelsAsync;

    public string Value(string key) => Config.Get(key) ?? "";

    public bool IsCyclable(string key) => key is "provider" or "saveSessions";

    /// <summary>Extra context for the selected row — where the API key comes from, what a value means.</summary>
    public string Hint() => SelectedKey switch
    {
        "provider" => "echo runs offline and exercises the real loop · openai talks to any OpenAI-compatible endpoint",
        "baseUrl" => "e.g. https://api.openai.com/v1 · http://localhost:11434/v1 (Ollama) · http://localhost:1234/v1 (LM Studio)",
        "model" => "Enter lists what the endpoint actually offers and lets you pick — an empty list means the key or the URL is wrong",
        "apiKeyEnv" => $"environment variable to read the key from — ${Config.ApiKeyEnv} is {(ApiKeyPresent ? "set" : "NOT set")}",
        "maxSteps" => "tool-loop budget per run, 1..100",
        "temperature" => "0..2 · lower is steadier, which suits a tool-calling loop",
        "timeoutSeconds" => "per-request timeout, 1..3600",
        "saveSessions" => "write a JSONL transcript per run under ~/.agent-one/sessions",
        _ => ""
    };

    public bool ApiKeyPresent =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Config.ApiKeyEnv));

    public TuiEffect HandleKey(ConsoleKeyInfo key)
    {
        // A request in flight owns the screen; only quit gets through.
        if (Busy)
        {
            if (key.Key is ConsoleKey.Escape or ConsoleKey.Q) return TuiEffect.Quit;
            return TuiEffect.None;
        }

        if (Editing) return HandleEditKey(key);
        if (Picking) return HandlePickKey(key);

        // Any non-quit key disarms a pending discard, so `q` then `j` then `q`
        // cannot silently throw work away.
        if (key.Key is not (ConsoleKey.Q or ConsoleKey.Escape)) QuitArmed = false;

        switch (key.Key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.K:
                Selected = Selected == 0 ? Keys.Count - 1 : Selected - 1;
                return TuiEffect.None;

            case ConsoleKey.DownArrow or ConsoleKey.J:
                Selected = (Selected + 1) % Keys.Count;
                return TuiEffect.None;

            case ConsoleKey.Home:
                Selected = 0;
                return TuiEffect.None;

            case ConsoleKey.End:
                Selected = Keys.Count - 1;
                return TuiEffect.None;

            case ConsoleKey.LeftArrow:
                Cycle(-1);
                return TuiEffect.None;

            case ConsoleKey.RightArrow:
                Cycle(+1);
                return TuiEffect.None;

            case ConsoleKey.Enter:
                // The model row is the one place where the endpoint knows better
                // than the operator what the valid values are — so ask it.
                if (SelectedKey == "model")
                {
                    Busy = true;
                    Status = $"asking {Config.BaseUrl}/models …";
                    return TuiEffect.FetchModels;
                }
                BeginEdit();
                return TuiEffect.None;

            case ConsoleKey.S:
                Save();
                return TuiEffect.None;

            case ConsoleKey.R:
                Reload();
                return TuiEffect.None;

            case ConsoleKey.D:
                RestoreDefaults();
                return TuiEffect.None;

            case ConsoleKey.T:
                Busy = true;
                Status = $"testing {Config.Provider} → {Config.Model} …";
                return TuiEffect.RunTest;

            case ConsoleKey.L:
                Busy = true;
                Status = $"asking {Config.BaseUrl}/models …";
                return TuiEffect.FetchModels;

            case ConsoleKey.Q or ConsoleKey.Escape:
                if (!Dirty || QuitArmed) return TuiEffect.Quit;
                QuitArmed = true;
                Status = "unsaved changes — q again to discard, or s to save";
                return TuiEffect.None;

            default:
                return TuiEffect.None;
        }
    }

    private TuiEffect HandleEditKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                Editing = false;
                EditBuffer = "";
                Status = "edit cancelled";
                return TuiEffect.None;

            case ConsoleKey.Enter:
                CommitEdit();
                return TuiEffect.None;

            case ConsoleKey.Backspace:
                if (EditBuffer.Length > 0) EditBuffer = EditBuffer[..^1];
                return TuiEffect.None;

            default:
                // Only printable characters; control keys are not text.
                if (!char.IsControl(key.KeyChar) && key.KeyChar != '\0')
                    EditBuffer += key.KeyChar;
                return TuiEffect.None;
        }
    }

    /// <summary>
    /// Which slice of <see cref="PickOptions"/> a picker <paramref name="height"/>
    /// rows tall should show, keeping the highlighted row inside it. The maths
    /// lives here rather than in the page so an endpoint offering eighty models
    /// cannot produce an off-by-one nobody sees until it does.
    /// </summary>
    public (int First, int Count) PickWindow(int height)
    {
        if (height <= 0 || PickOptions.Count == 0) return (0, 0);
        if (PickOptions.Count <= height) return (0, PickOptions.Count);

        var first = PickIndex - height / 2;
        first = Math.Clamp(first, 0, PickOptions.Count - height);
        return (first, height);
    }

    private TuiEffect HandlePickKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.K:
                PickIndex = PickIndex == 0 ? PickOptions.Count - 1 : PickIndex - 1;
                return TuiEffect.None;

            case ConsoleKey.DownArrow or ConsoleKey.J:
                PickIndex = (PickIndex + 1) % PickOptions.Count;
                return TuiEffect.None;

            case ConsoleKey.Home:
                PickIndex = 0;
                return TuiEffect.None;

            case ConsoleKey.End:
                PickIndex = PickOptions.Count - 1;
                return TuiEffect.None;

            case ConsoleKey.Enter:
                ChoosePicked();
                return TuiEffect.None;

            case ConsoleKey.Escape or ConsoleKey.Q:
                Picking = false;
                PickOptions = [];
                Status = "model unchanged";
                return TuiEffect.None;

            default:
                return TuiEffect.None;
        }
    }

    private void ChoosePicked()
    {
        var chosen = PickOptions[PickIndex];
        Picking = false;
        PickOptions = [];

        // The endpoint offered nothing that fits, or the list is stale — fall
        // through to the ordinary text editor rather than a dead end.
        if (chosen == PickManualEntry)
        {
            BeginEdit();
            return;
        }

        if (Config.TrySet("model", chosen, out var error))
            Status = $"model = {chosen}" + (Dirty ? "  (unsaved — press s)" : "");
        else
            Status = "✗ " + error;
    }

    /// <summary>
    /// Called by the shell with whatever the endpoint said. A failure is not an
    /// error state to recover from — it is the answer the operator asked for, so
    /// it goes on the status line and the screen carries on.
    /// </summary>
    public void CompleteModelFetch(ModelCatalogResult result)
    {
        Busy = false;

        if (!result.Ok || result.Models.Count == 0)
        {
            Picking = false;
            PickOptions = [];
            Status = "✗ " + result.Message + " — check baseUrl and $" + Config.ApiKeyEnv;
            return;
        }

        var options = new List<string>(result.Models) { PickManualEntry };
        PickOptions = options;

        // Start on the model already configured, so Enter twice is a no-op
        // rather than a surprise.
        var current = options.IndexOf(Value("model"));
        PickIndex = current >= 0 ? current : 0;

        Picking = true;
        Status = $"✓ {result.Message} · ↑↓ to choose, Enter to take it, Esc to keep {Value("model")}";
    }

    private void BeginEdit()
    {
        Editing = true;
        EditBuffer = Value(SelectedKey);
        Status = $"editing {SelectedKey} — Enter to accept, Esc to cancel";
    }

    private void CommitEdit()
    {
        var key = SelectedKey;
        if (Config.TrySet(key, EditBuffer.Trim(), out var error))
        {
            Editing = false;
            EditBuffer = "";
            Status = $"{key} = {Value(key)}" + (Dirty ? "  (unsaved)" : "");
        }
        else
        {
            // Stay in edit mode: the buffer is still what the user typed, so they
            // can fix it instead of retyping from scratch.
            Status = "✗ " + error;
        }
    }

    private void Cycle(int direction)
    {
        var key = SelectedKey;
        string[]? options = key switch
        {
            "provider" => ["echo", "openai"],
            "saveSessions" => ["true", "false"],
            _ => null
        };

        if (options is null)
        {
            Status = $"{key} is free text — press Enter to edit";
            return;
        }

        var current = Array.IndexOf(options, Value(key));
        var next = options[((current < 0 ? 0 : current) + direction + options.Length) % options.Length];

        if (Config.TrySet(key, next, out var error))
            Status = $"{key} = {Value(key)}" + (Dirty ? "  (unsaved)" : "");
        else
            Status = "✗ " + error;
    }

    public void Save()
    {
        try
        {
            ConfigStore.Save(Config);
            _saved = Snapshot(Config);
            QuitArmed = false;
            Status = $"saved → {AppPaths.ConfigPath}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "✗ could not save: " + ex.Message;
        }
    }

    public void Reload()
    {
        Config = ConfigStore.Load(out var warning);
        _saved = Snapshot(Config);
        QuitArmed = false;
        Status = warning.Length > 0 ? warning : "reloaded from disk";
    }

    public void RestoreDefaults()
    {
        Config = new AgentConfig();
        Status = "defaults restored in memory — press s to write them";
    }

    public void CompleteTest(string message)
    {
        Busy = false;
        Status = message;
    }

    private static Dictionary<string, string> Snapshot(AgentConfig config) =>
        AgentConfig.Keys.ToDictionary(k => k, k => config.Get(k) ?? "", StringComparer.Ordinal);
}
