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

/// <summary>The settings, in the order you have to know them.</summary>
public enum ConfigStep
{
    /// <summary>Where to talk to and how to authenticate. Nothing else is knowable until this is right.</summary>
    Connection = 0,
    /// <summary>What that endpoint can run — asked, not typed.</summary>
    Model = 1,
    /// <summary>How the loop behaves. Safe defaults, so it comes last.</summary>
    Options = 2
}

/// <summary>
/// The whole settings screen as a state machine, with no Termina and no console
/// in sight. Keeping it here means the key map itself is unit-tested — feed it
/// a <see cref="ConsoleKeyInfo"/>, assert on the state — while the Termina page
/// stays a thin renderer over these properties.
///
/// The screen is a three-step stack rather than one flat list because the
/// settings genuinely depend on each other: you cannot pick a model until the
/// endpoint and key are right, and the endpoint is the thing that knows which
/// models exist. Entering the Model step therefore asks it — which is also the
/// health check for the step before.
/// </summary>
public sealed class ConfigTuiModel
{
    public static readonly string[] StepTitles = ["Connection", "Model", "Options"];

    /// <summary>The config keys each step owns. The Model step is the picker, so it has none.</summary>
    public static readonly string[][] StepFields =
    [
        ["provider", "baseUrl", "apiKey", "apiKeyEnv"],
        [],
        ["maxSteps", "temperature", "timeoutSeconds", "saveSessions"]
    ];

    /// <summary>The key typed on this screen, held until save. Null means untouched.</summary>
    private string? _pendingKey;

    private Dictionary<string, string> _saved;

    public ConfigTuiModel(AgentConfig config)
    {
        Config = config;
        _saved = Snapshot(config);
        Status = "↑↓ to move · Enter to edit · Tab for the model step";
    }

    public static ConfigTuiModel Load()
    {
        var config = ConfigStore.Load(out var warning);
        var model = new ConfigTuiModel(config);
        if (warning.Length > 0) model.Status = warning;
        return model;
    }

    public AgentConfig Config { get; private set; }

    public ConfigStep Step { get; private set; } = ConfigStep.Connection;

    /// <summary>The fields of the current step. Empty on the Model step.</summary>
    public IReadOnlyList<string> Fields => StepFields[(int)Step];

    public int Selected { get; private set; }

    /// <summary>The config key the cursor is on — "model" while the Model step is showing.</summary>
    public string SelectedKey => Fields.Count == 0 ? "model" : Fields[Math.Clamp(Selected, 0, Fields.Count - 1)];

    public bool Editing { get; private set; }

    public string EditBuffer { get; private set; } = "";

    /// <summary>True while a request is in flight; keys other than quit are ignored.</summary>
    public bool Busy { get; private set; }

    /// <summary>True while a model list is on screen.</summary>
    public bool Picking { get; private set; }

    /// <summary>Model ids offered by the endpoint, plus a final "type it myself" entry.</summary>
    public IReadOnlyList<string> PickOptions { get; private set; } = [];

    public int PickIndex { get; private set; }

    /// <summary>The sentinel last row of the picker — choosing it falls back to free-text entry.</summary>
    public const string PickManualEntry = "· type a model id myself ·";

    public string Status { get; private set; }

    /// <summary>Set when any field, or the key, differs from what is on disk.</summary>
    public bool Dirty => _pendingKey is not null || AgentConfig.Keys.Any(k => _saved[k] != Config.Get(k));

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

    /// <summary>
    /// The apiKey row is not a config field — it is the credential store, shown
    /// masked. Everything else comes straight from the config.
    /// </summary>
    public string Value(string key) => key == ApiKeyField
        ? CredentialStore.Mask(_pendingKey ?? CredentialStore.Load())
        : Config.Get(key) ?? "";

    /// <summary>The pseudo-field name for the key itself.</summary>
    public const string ApiKeyField = "apiKey";

    public bool IsCyclable(string key) => key is "provider" or "saveSessions";

    public bool ApiKeyPresent =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Config.ApiKeyEnv));

    /// <summary>Extra context for whatever the cursor is on.</summary>
    public string Hint() => Step switch
    {
        ConfigStep.Model when Picking =>
            "the list came from the endpoint itself — picking from it cannot be a typo",
        ConfigStep.Model =>
            "no list yet · b to go back and fix the connection · l to ask again · e to type an id by hand",
        _ => FieldHint(SelectedKey)
    };

    private string FieldHint(string key) => key switch
    {
        "provider" => "echo runs offline and exercises the real loop · openai talks to any OpenAI-compatible endpoint",
        "baseUrl" => "https://api.openai.com/v1 · http://localhost:11434/v1 (Ollama) · http://localhost:1234/v1 (LM Studio)",
        ApiKeyField => "paste the key itself here — it is stored in ~/.agent-one/credentials.json, never in config.json",
        "apiKeyEnv" => $"FALLBACK only — the NAME of a variable to read the key from, not the key. ${Config.ApiKeyEnv} is {(ApiKeyPresent ? "set" : "not set")}",
        "model" => "set on the Model step",
        "maxSteps" => "tool-loop budget per run, 1..100",
        "temperature" => "0..2 · lower is steadier, which suits a tool-calling loop",
        "timeoutSeconds" => "per-request timeout, 1..3600",
        "saveSessions" => "write a JSONL transcript per run under ~/.agent-one/sessions",
        _ => ""
    };

    // ---------------------------------------------------------------- keys

    public TuiEffect HandleKey(ConsoleKeyInfo key)
    {
        // A request in flight owns the screen; only quit gets through.
        if (Busy)
        {
            if (key.Key is ConsoleKey.Escape or ConsoleKey.Q) return TuiEffect.Quit;
            return TuiEffect.None;
        }

        if (Editing) return HandleEditKey(key);

        // Any non-quit key disarms a pending discard, so `q` then `j` then `q`
        // cannot silently throw work away.
        if (key.Key is not (ConsoleKey.Q or ConsoleKey.Escape)) QuitArmed = false;

        // Step navigation works the same on every step.
        switch (key.Key)
        {
            case ConsoleKey.Tab when key.Modifiers.HasFlag(ConsoleModifiers.Shift):
            case ConsoleKey.B:
            case ConsoleKey.PageUp:
                return GoToStep((int)Step - 1);

            case ConsoleKey.Tab:
            case ConsoleKey.N:
            case ConsoleKey.PageDown:
                return GoToStep((int)Step + 1);

            case ConsoleKey.S:
                Save();
                return TuiEffect.None;

            case ConsoleKey.R:
                Reload();
                return TuiEffect.None;

            case ConsoleKey.T:
                Busy = true;
                Status = $"testing {Config.Provider} → {Config.Model} …";
                return TuiEffect.RunTest;

            case ConsoleKey.L:
                Busy = true;
                Status = $"asking {Config.BaseUrl}/models …";
                return TuiEffect.FetchModels;

            // Esc means "back" on a stack, so it only leaves from the first step.
            case ConsoleKey.Escape when Step != ConfigStep.Connection:
                return GoToStep((int)Step - 1);

            case ConsoleKey.Q or ConsoleKey.Escape:
                if (!Dirty || QuitArmed) return TuiEffect.Quit;
                QuitArmed = true;
                Status = "unsaved changes — q again to discard, or s to save";
                return TuiEffect.None;
        }

        return Step == ConfigStep.Model ? HandleModelStepKey(key) : HandleFieldKey(key);
    }

    private TuiEffect HandleFieldKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.K:
                Selected = Selected == 0 ? Fields.Count - 1 : Selected - 1;
                return TuiEffect.None;

            case ConsoleKey.DownArrow or ConsoleKey.J:
                Selected = (Selected + 1) % Fields.Count;
                return TuiEffect.None;

            case ConsoleKey.Home:
                Selected = 0;
                return TuiEffect.None;

            case ConsoleKey.End:
                Selected = Fields.Count - 1;
                return TuiEffect.None;

            case ConsoleKey.LeftArrow:
                Cycle(-1);
                return TuiEffect.None;

            case ConsoleKey.RightArrow:
                Cycle(+1);
                return TuiEffect.None;

            case ConsoleKey.Enter:
                BeginEdit();
                return TuiEffect.None;

            case ConsoleKey.D:
                RestoreDefaults();
                return TuiEffect.None;

            default:
                return TuiEffect.None;
        }
    }

    private TuiEffect HandleModelStepKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.K when Picking:
                PickIndex = PickIndex == 0 ? PickOptions.Count - 1 : PickIndex - 1;
                return TuiEffect.None;

            case ConsoleKey.DownArrow or ConsoleKey.J when Picking:
                PickIndex = (PickIndex + 1) % PickOptions.Count;
                return TuiEffect.None;

            case ConsoleKey.Home when Picking:
                PickIndex = 0;
                return TuiEffect.None;

            case ConsoleKey.End when Picking:
                PickIndex = PickOptions.Count - 1;
                return TuiEffect.None;

            case ConsoleKey.Enter when Picking:
                ChoosePicked();
                return TuiEffect.None;

            // Always available: the list can be wrong, stale, or missing entirely.
            case ConsoleKey.E:
            case ConsoleKey.Enter:
                BeginEdit();
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

    // ---------------------------------------------------------------- steps

    /// <summary>
    /// Moves between steps, clamped at both ends. Arriving at the Model step
    /// asks the endpoint for its list — that request is the step, and it is also
    /// what proves the Connection step was filled in correctly.
    /// </summary>
    private TuiEffect GoToStep(int index)
    {
        var target = (ConfigStep)Math.Clamp(index, 0, StepTitles.Length - 1);

        if (target == Step)
        {
            Status = index < 0 ? "already on the first step" : "last step — s to save, q to quit";
            return TuiEffect.None;
        }

        Step = target;
        Selected = 0;
        Editing = false;
        EditBuffer = "";

        if (target != ConfigStep.Model)
        {
            Picking = false;
            PickOptions = [];
            Status = $"step {(int)Step + 1}/{StepTitles.Length} — {StepTitles[(int)Step]}";
            return TuiEffect.None;
        }

        Busy = true;
        Picking = false;
        PickOptions = [];
        Status = $"asking {Config.BaseUrl}/models …";
        return TuiEffect.FetchModels;
    }

    /// <summary>Test seam: enter a step directly, as a key press would.</summary>
    public TuiEffect JumpToStep(ConfigStep step) => GoToStep((int)step);

    // ---------------------------------------------------------------- picker

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

        var first = Math.Clamp(PickIndex - height / 2, 0, PickOptions.Count - height);
        return (first, height);
    }

    private void ChoosePicked()
    {
        var chosen = PickOptions[PickIndex];

        // The endpoint offered nothing that fits, or the list is stale — fall
        // through to the ordinary text editor rather than a dead end.
        if (chosen == PickManualEntry)
        {
            BeginEdit();
            return;
        }

        // The picker stays open — it is this step's body, and the choice is now
        // marked (current), so a mis-pick is one keystroke to undo.
        if (Config.TrySet("model", chosen, out var error))
            Status = $"model = {chosen}" + (Dirty ? "  (unsaved)" : "") + " · Tab for options";
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
            Status = $"✗ {result.Message} — check baseUrl and ${Config.ApiKeyEnv} · b to go back, e to type an id";
            return;
        }

        var options = new List<string>(result.Models) { PickManualEntry };
        PickOptions = options;

        // Start on the model already configured, so Enter twice is a no-op
        // rather than a surprise.
        var current = options.IndexOf(Value("model"));
        PickIndex = current >= 0 ? current : 0;

        Picking = true;
        Status = $"✓ {result.Message}";
    }

    public void CompleteTest(string message)
    {
        Busy = false;
        Status = message;
    }

    // ---------------------------------------------------------------- editing

    private void BeginEdit()
    {
        Editing = true;
        // The key row shows a mask, which would be nonsense to edit in place.
        EditBuffer = SelectedKey == ApiKeyField ? "" : Value(SelectedKey);
        Status = $"editing {SelectedKey} — Enter to accept, Esc to cancel";
    }

    private void CommitEdit()
    {
        var key = SelectedKey;

        if (key == ApiKeyField)
        {
            var typed = EditBuffer.Trim();
            Editing = false;
            EditBuffer = "";

            if (typed.Length == 0)
            {
                Status = "key unchanged";
                return;
            }

            _pendingKey = typed;
            Status = $"key set to {CredentialStore.Mask(typed)} — press s to store it";
            return;
        }

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

    // ---------------------------------------------------------------- store

    public void Save()
    {
        try
        {
            if (_pendingKey is not null)
            {
                CredentialStore.Save(_pendingKey);
                _pendingKey = null;
            }

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
        _pendingKey = null;
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

    private static Dictionary<string, string> Snapshot(AgentConfig config) =>
        AgentConfig.Keys.ToDictionary(k => k, k => config.Get(k) ?? "", StringComparer.Ordinal);
}
