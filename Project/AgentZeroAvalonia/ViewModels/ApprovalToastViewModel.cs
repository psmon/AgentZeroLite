using System.Collections.ObjectModel;
using Agent.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>One answer the approval prompt offers, with the index the responder needs.</summary>
public sealed record ToastOption(int Index, string Label);

/// <summary>
/// The approval overlay (M0041), ported from the WPF host's <c>ApprovalToast</c> user control.
/// A terminal asking "may I run this?" is the one moment the bot must interrupt, so the toast
/// sits over the transcript, counts down, and gets out of the way on its own.
/// </summary>
/// <remarks>
/// The countdown runs on the injected <see cref="Delay"/> seam rather than a timer, so the
/// tests advance it without waiting. Mute is a five-minute snooze, not a setting — it exists
/// for the case where a terminal is spamming prompts and the user wants quiet, not a
/// permanent change of behaviour.
/// </remarks>
public partial class ApprovalToastViewModel : ObservableObject
{
    /// <summary>The toast hides itself after this long.</summary>
    public const int AutoHideSeconds = 10;

    /// <summary>How long "mute" suppresses new toasts.</summary>
    public static readonly TimeSpan MuteFor = TimeSpan.FromMinutes(5);

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private int _remainingSeconds;

    public ObservableCollection<ToastOption> Options { get; } = new();

    /// <summary>Injected clock, so the countdown is testable. Defaults to real time.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (t, c) => Task.Delay(t, c);

    /// <summary>Injected "now", so mute expiry is testable.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Marshals the countdown back to the UI thread; the tests leave it synchronous.</summary>
    public Action<Action> Post { get; set; } = a => a();

    /// <summary>Raised with the 0-based option index the user picked.</summary>
    public event Action<int>? OptionSelected;

    private DateTimeOffset? _muteUntil;
    private CancellationTokenSource? _countdown;

    /// <summary>True while the five-minute snooze is running.</summary>
    public bool IsMuted => _muteUntil is { } until && Now() < until;

    /// <summary>
    /// Shows the prompt. Does nothing while muted — the caller still gets to log it, which is
    /// why this returns whether it opened.
    /// </summary>
    public bool Show(string command, IReadOnlyList<ToastOption> options)
    {
        if (IsMuted) return false;

        Command = string.IsNullOrEmpty(command) ? "(unknown command)" : command;
        Options.Clear();
        foreach (var option in options) Options.Add(option);

        RemainingSeconds = AutoHideSeconds;
        IsOpen = true;

        StartCountdown();
        return true;
    }

    [RelayCommand]
    public void Hide()
    {
        _countdown?.Cancel();
        _countdown = null;
        IsOpen = false;
    }

    /// <summary>Answer with one of the offered options and close.</summary>
    public void Choose(int index)
    {
        Hide();
        OptionSelected?.Invoke(index);
    }

    [RelayCommand]
    private void SelectOption(ToastOption? option)
    {
        if (option is not null) Choose(option.Index);
    }

    [RelayCommand]
    public void Mute()
    {
        _muteUntil = Now() + MuteFor;
        Hide();
    }

    private void StartCountdown()
    {
        _countdown?.Cancel();
        var cts = new CancellationTokenSource();
        _countdown = cts;
        _ = RunCountdownAsync(cts.Token);
    }

    private async Task RunCountdownAsync(CancellationToken ct)
    {
        try
        {
            while (RemainingSeconds > 0 && !ct.IsCancellationRequested)
            {
                await Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                Post(() => RemainingSeconds--);
            }
            if (!ct.IsCancellationRequested) Post(Hide);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Log($"[Bot] approval countdown failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
