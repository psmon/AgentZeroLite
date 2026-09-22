using R3;
using Termina.Input;
using Termina.Reactive;

namespace AgentOne.Tui;

/// <summary>
/// Thin reactive wrapper over <see cref="ConfigTuiModel"/>. It owns no rules of
/// its own: keys go straight to the model, and a single Revision bump tells the
/// page to rebuild. Everything worth testing lives one layer down, where no
/// terminal is needed.
/// </summary>
public sealed class ConfigTuiViewModel : ReactiveViewModel
{
    private readonly CancellationTokenSource _cts = new();

    public ConfigTuiViewModel() : this(ConfigTuiModel.Load()) { }

    public ConfigTuiViewModel(ConfigTuiModel model) => Model = model;

    public ConfigTuiModel Model { get; }

    /// <summary>Incremented after every state change so the page re-renders.</summary>
    public ReactiveProperty<int> Revision { get; } = new(0);

    public override void OnActivated()
    {
        Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKey)
            .DisposeWith(Subscriptions);
    }

    private void HandleKey(KeyPressed key)
    {
        var effect = Model.HandleKey(key.KeyInfo);
        Revision.Value++;

        switch (effect)
        {
            case TuiEffect.Quit:
                _cts.Cancel();
                Shutdown();
                break;

            case TuiEffect.RunTest:
                _ = RunTestAsync();
                break;

            case TuiEffect.FetchModels:
                _ = FetchModelsAsync();
                break;

            case TuiEffect.CheckSmart:
                _ = RunSmartCheckAsync();
                break;
        }
    }

    private async Task RunSmartCheckAsync()
    {
        string message;
        try
        {
            message = await Model.SmartCheck(Model.Config, _cts.Token);
        }
        catch (Exception ex)
        {
            message = "✗ " + ex.Message;
        }

        Model.CompleteTest(message);
        Revision.Value++;
    }

    private async Task FetchModelsAsync()
    {
        Llm.ModelCatalogResult result;
        try
        {
            result = await Model.ModelCatalog(Model.Config, _cts.Token);
        }
        catch (Exception ex)
        {
            result = Llm.ModelCatalogResult.Failure(ex.Message);
        }

        Model.CompleteModelFetch(result);
        Revision.Value++;
    }

    private async Task RunTestAsync()
    {
        string message;
        try
        {
            message = await Model.ConnectionTest(Model.Config, _cts.Token);
        }
        catch (Exception ex)
        {
            // A probe must never take the screen down with it.
            message = "✗ " + ex.Message;
        }

        Model.CompleteTest(message);
        Revision.Value++;
    }

    public override void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        Revision.Dispose();
        base.Dispose();
    }
}
