using System.Windows;
using System.Windows.Controls;
using Agent.Common;
using Agent.Common.Llm;
using Agent.Common.Llm.Providers;

namespace AgentZeroWpf.UI.Components;

public partial class SettingsPanel
{
    // Suppresses cosmetic side-effects (e.g. "Settings unsaved" toast,
    // auto-save) while InitializeExternalTab is hydrating editors from disk.
    // Without this, programmatically setting cbExtProvider.SelectedIndex on
    // panel-open fires SelectionChanged → status flips to "unsaved" before
    // the user has touched anything.
    private bool _extInitializing;

    // RadioButton.Checked fires for the new selection only — we don't get a
    // "previously" event, but we also don't need one: the new state is the
    // source of truth and we re-render either way.
    private void OnActiveBackendChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _extInitializing) return;  // initial Loaded handler hydrates this
        var settings = LlmSettingsStore.Load();
        var newBackend = rbBackendExternal.IsChecked == true
            ? LlmActiveBackend.External
            : LlmActiveBackend.Local;
        if (settings.ActiveBackend == newBackend) return;

        settings.ActiveBackend = newBackend;
        LlmSettingsStore.Save(settings);
        AppLogger.Log($"[LLM] Active backend → {newBackend}");
        UpdateActiveBackendStatus();

        // Auto-switch the sub-tab so the user sees the relevant config.
        if (tcLlmBackend is not null)
            tcLlmBackend.SelectedIndex = newBackend == LlmActiveBackend.Local ? 0 : 1;
    }

    private void OnLlmBackendSubTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // Sub-tab change is purely cosmetic — it does NOT flip ActiveBackend.
        // Users use the radio buttons above for that. This handler exists so
        // we can update the small status hint when the user inspects a tab.
        UpdateActiveBackendStatus();
    }

    private void UpdateActiveBackendStatus()
    {
        if (tbActiveBackendStatus is null) return;
        var s = LlmSettingsStore.Load();
        if (s.ActiveBackend == LlmActiveBackend.Local)
        {
            var entry = LlmModelCatalog.FindById(s.ModelId);
            tbActiveBackendStatus.Text = $"→ Local · {entry.DisplayName}";
        }
        else
        {
            var model = s.ResolveExternalModel();
            tbActiveBackendStatus.Text = $"→ External · {s.External.Provider} · {(string.IsNullOrEmpty(model) ? "(no model)" : model)}";
        }
    }

    /// <summary>
    /// Hydrates the External sub-tab editors from persisted settings. Called
    /// from <see cref="InitializeLlmTab"/> alongside the local hydration.
    /// </summary>
    private void InitializeExternalTab()
    {
        _extInitializing = true;
        try
        {
            var s = LlmSettingsStore.Load();

            // Active-backend radio buttons
            rbBackendLocal.IsChecked = s.ActiveBackend == LlmActiveBackend.Local;
            rbBackendExternal.IsChecked = s.ActiveBackend == LlmActiveBackend.External;
            tcLlmBackend.SelectedIndex = s.ActiveBackend == LlmActiveBackend.Local ? 0 : 1;

            // Provider dropdown
            cbExtProvider.SelectedIndex = s.External.Provider switch
            {
                ExternalProviderNames.Webnori => 0,
                ExternalProviderNames.WebnoriA2 => 1,
                ExternalProviderNames.OpenAI => 2,
                ExternalProviderNames.LMStudio => 3,
                ExternalProviderNames.Ollama => 4,
                _ => 0,
            };

            ApplyProviderSlotsToUi(s);

            // Seed model dropdown FIRST so the saved id can match a list entry
            // (lets non-editable callers see SelectionBoxItem; editable callers
            // get the typed Text either way).
            if (s.External.Provider == ExternalProviderNames.Webnori)
                PopulateModelDropdown(WebnoriDefaults.KnownModels);
            else if (s.External.Provider == ExternalProviderNames.WebnoriA2)
                PopulateModelDropdown(WebnoriDefaults.KnownModelsA2);
            else
                cbExtModel.Items.Clear();

            cbExtModel.Text = string.IsNullOrEmpty(s.External.SelectedModel)
                ? s.External.Provider switch
                {
                    ExternalProviderNames.Webnori => WebnoriDefaults.DefaultModel,
                    ExternalProviderNames.WebnoriA2 => WebnoriDefaults.DefaultModelA2,
                    _ => "",
                }
                : s.External.SelectedModel;

            tbExtMaxTokens.Text = s.External.MaxTokens.ToString();

            UpdateActiveBackendStatus();
        }
        finally
        {
            _extInitializing = false;
        }
    }

    private void OnExtProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _extInitializing) return;
        var s = LlmSettingsStore.Load();
        s.External.Provider = ProviderFromIndex(cbExtProvider.SelectedIndex);
        ApplyProviderSlotsToUi(s);

        // Re-seed model dropdown for the new provider.
        if (s.External.Provider == ExternalProviderNames.Webnori)
        {
            PopulateModelDropdown(WebnoriDefaults.KnownModels);
            cbExtModel.Text = string.IsNullOrEmpty(s.External.SelectedModel)
                ? WebnoriDefaults.DefaultModel
                : s.External.SelectedModel;
        }
        else if (s.External.Provider == ExternalProviderNames.WebnoriA2)
        {
            PopulateModelDropdown(WebnoriDefaults.KnownModelsA2);
            cbExtModel.Text = string.IsNullOrEmpty(s.External.SelectedModel)
                ? WebnoriDefaults.DefaultModelA2
                : s.External.SelectedModel;
        }
        else
        {
            cbExtModel.Items.Clear();
            cbExtModel.Text = s.External.SelectedModel;
        }

        // We don't auto-save the provider change — user clicks Save Options.
        // But the per-provider URL/Key fields *visually* swap so the user
        // sees the right slot's content immediately.
        tbExtStatus.Text = "Settings unsaved — click Save Options.";
        tbExtStatus.Foreground = System.Windows.Media.Brushes.Goldenrod;
    }

    private void OnExtModelChanged(object sender, SelectionChangedEventArgs e)
    {
        // No-op: persisted on Save Options. Editable ComboBox lets users type
        // arbitrary model ids when the provider's /v1/models doesn't expose
        // the one they want (rare for OpenAI, common for niche LM Studio loads).
    }

    private async void OnExtRefreshModelsClick(object sender, RoutedEventArgs e)
    {
        var s = ReadExternalFromUi();
        var provider = s.CreateExternalProvider();
        if (provider is null)
        {
            tbExtStatus.Text = $"Provider '{s.External.Provider}' not recognised.";
            tbExtStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            return;
        }
        btnExtRefreshModels.IsEnabled = false;
        tbExtStatus.Text = "Listing models…";
        tbExtStatus.Foreground = System.Windows.Media.Brushes.SkyBlue;
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
            var models = await provider.ListModelsAsync(cts.Token);
            PopulateModelDropdown(models.ConvertAll(m => m.Id));
            tbExtStatus.Text = $"✓ {models.Count} model(s) listed.";
            tbExtStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        catch (Exception ex)
        {
            tbExtStatus.Text = $"✗ Refresh failed: {ex.Message}";
            tbExtStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            AppLogger.LogError("[LLM-EXT] ListModels failed", ex);
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
            btnExtRefreshModels.IsEnabled = true;
        }
    }

    private void OnExtSaveClick(object sender, RoutedEventArgs e)
    {
        var s = ReadExternalFromUi();
        LlmSettingsStore.Save(s);
        tbExtStatus.Text = "✓ Saved.";
        tbExtStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
        UpdateActiveBackendStatus();
        AppLogger.Log($"[LLM-EXT] Saved provider={s.External.Provider} model={s.External.SelectedModel} maxTok={s.External.MaxTokens}");
    }

    private async void OnExtTestClick(object sender, RoutedEventArgs e)
    {
        var s = ReadExternalFromUi();
        var provider = s.CreateExternalProvider();
        if (provider is null)
        {
            tbExtStatus.Text = $"✗ Provider '{s.External.Provider}' not recognised.";
            tbExtStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            return;
        }
        var model = s.ResolveExternalModel();
        if (string.IsNullOrEmpty(model))
        {
            (provider as IDisposable)?.Dispose();
            tbExtStatus.Text = "✗ Pick a model first.";
            tbExtStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            return;
        }
        btnExtTest.IsEnabled = false;
        tbExtStatus.Text = "Testing…";
        tbExtStatus.Foreground = System.Windows.Media.Brushes.SkyBlue;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            var resp = await provider.CompleteAsync(new LlmRequest
            {
                Model = model,
                Messages = [LlmMessage.User("Reply with exactly one word: hello")],
                Temperature = 0.0f,
                MaxTokens = 16,
            }, cts.Token);
            sw.Stop();
            var preview = resp.Text.Length > 60 ? resp.Text[..60] + "…" : resp.Text;
            tbExtStatus.Text = $"✓ {sw.ElapsedMilliseconds}ms · \"{preview}\"";
            tbExtStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        catch (Exception ex)
        {
            tbExtStatus.Text = $"✗ Test failed: {ex.Message}";
            tbExtStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            AppLogger.LogError("[LLM-EXT] Test failed", ex);
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
            btnExtTest.IsEnabled = true;
        }
    }

    // ── helpers ──

    private static string ProviderFromIndex(int idx) => idx switch
    {
        0 => ExternalProviderNames.Webnori,
        1 => ExternalProviderNames.WebnoriA2,
        2 => ExternalProviderNames.OpenAI,
        3 => ExternalProviderNames.LMStudio,
        4 => ExternalProviderNames.Ollama,
        _ => ExternalProviderNames.Webnori,
    };

    private void ApplyProviderSlotsToUi(LlmRuntimeSettings s)
    {
        switch (s.External.Provider)
        {
            case ExternalProviderNames.Webnori:
                tbExtBaseUrl.Text = WebnoriDefaults.BaseUrl;
                tbExtApiKey.Text = WebnoriDefaults.ApiKey;
                tbExtBaseUrl.IsReadOnly = true;
                tbExtApiKey.IsReadOnly = true;
                tbExtProviderHint.Text = "🧪 Webnori a1 — workhorse host (Gemma 4 / GPT-OSS / embeddings). Bundled test key shipped with the app so any AgentZero Lite user can try the External LLM path immediately without setting up their own credentials. Endpoint accepts unauthenticated calls too; the bundled key is shipped on purpose (not a secret) and is included on every request for consistent identification while you evaluate the app. For sustained / production use, switch to Local or your own provider.";
                break;
            case ExternalProviderNames.WebnoriA2:
                tbExtBaseUrl.Text = WebnoriDefaults.BaseUrlA2;
                tbExtApiKey.Text = WebnoriDefaults.ApiKey;
                tbExtBaseUrl.IsReadOnly = true;
                tbExtApiKey.IsReadOnly = true;
                tbExtProviderHint.Text = "🧪 Webnori a2 — experimental comparison group (Qwen3.6-27B / Nemotron-3-Nano-4B). Same bundled test key as a1; pick this when you want to A/B against a non-Gemma model. Note: AgentZero's AIMODE toolchain is Gemma-4-shaped — non-Gemma models on a2 may struggle with the JSON envelope. Best used from the LLM PlayGround for free-form chat comparison.";
                break;
            case ExternalProviderNames.OpenAI:
                tbExtBaseUrl.Text = s.External.OpenAIBaseUrl;
                tbExtApiKey.Text = s.External.OpenAIApiKey;
                tbExtBaseUrl.IsReadOnly = false;
                tbExtApiKey.IsReadOnly = false;
                tbExtProviderHint.Text = "Requires an API key from platform.openai.com. Leave Base URL empty for the official endpoint.";
                break;
            case ExternalProviderNames.LMStudio:
                tbExtBaseUrl.Text = s.External.LMStudioBaseUrl;
                tbExtApiKey.Text = s.External.LMStudioApiKey;
                tbExtBaseUrl.IsReadOnly = false;
                tbExtApiKey.IsReadOnly = false;
                tbExtProviderHint.Text = "Point at your own LM Studio server (default port 1234). Key is optional unless you've set one server-side.";
                break;
            case ExternalProviderNames.Ollama:
                tbExtBaseUrl.Text = s.External.OllamaBaseUrl;
                tbExtApiKey.Text = "";
                tbExtBaseUrl.IsReadOnly = false;
                tbExtApiKey.IsReadOnly = true;
                tbExtProviderHint.Text = "Local Ollama daemon. Empty Base URL defaults to http://localhost:11434. No key.";
                break;
        }
    }

    private void PopulateModelDropdown(IEnumerable<string> ids)
    {
        var current = cbExtModel.Text;
        cbExtModel.Items.Clear();
        foreach (var id in ids)
            cbExtModel.Items.Add(id);
        cbExtModel.Text = current; // preserve typed value
    }

    private LlmRuntimeSettings ReadExternalFromUi()
    {
        var s = LlmSettingsStore.Load();
        s.ActiveBackend = rbBackendExternal.IsChecked == true
            ? LlmActiveBackend.External
            : LlmActiveBackend.Local;
        s.External.Provider = ProviderFromIndex(cbExtProvider.SelectedIndex);

        // Persist to the matching per-provider slot — Webnori is hardcoded.
        switch (s.External.Provider)
        {
            case ExternalProviderNames.OpenAI:
                s.External.OpenAIBaseUrl = tbExtBaseUrl.Text.Trim();
                s.External.OpenAIApiKey = tbExtApiKey.Text.Trim();
                break;
            case ExternalProviderNames.LMStudio:
                s.External.LMStudioBaseUrl = tbExtBaseUrl.Text.Trim();
                s.External.LMStudioApiKey = tbExtApiKey.Text.Trim();
                break;
            case ExternalProviderNames.Ollama:
                s.External.OllamaBaseUrl = tbExtBaseUrl.Text.Trim();
                break;
        }

        s.External.SelectedModel = cbExtModel.Text.Trim();
        if (int.TryParse(tbExtMaxTokens.Text, out var mt) && mt > 0)
            s.External.MaxTokens = mt;
        return s;
    }
}
