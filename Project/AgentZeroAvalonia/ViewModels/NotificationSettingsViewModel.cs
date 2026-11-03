using Agent.Common;
using Agent.Common.Agents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// Settings → Notifications: when an agent starts needing the user (blocked, or a finished turn
/// not yet seen), pop up in the screen corner and flash the taskbar / bounce the Dock.
/// The rules are ZeroCommon's <see cref="AttentionNotifier"/>; this page edits
/// <c>attention-settings.json</c>.
/// </summary>
public partial class NotificationSettingsViewModel : ObservableObject
{
    private readonly string? _path;

    public NotificationSettingsViewModel(string? path = null)
    {
        _path = path;
        Load();
    }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _onBlocked;
    [ObservableProperty] private bool _onDone;
    [ObservableProperty] private bool _onNotify;
    [ObservableProperty] private bool _onlyWhenInactive;
    [ObservableProperty] private bool _flashWindow;
    [ObservableProperty] private bool _popup;
    [ObservableProperty] private decimal? _popupSeconds;
    [ObservableProperty] private string _status = "";

    public string FlashLabel => OperatingSystem.IsMacOS()
        ? "Bounce the Dock icon"
        : OperatingSystem.IsWindows() ? "Flash the taskbar button" : "Flash the window (not available on Linux — the popup is the signal)";

    /// <summary>Saved — the main window re-reads the file.</summary>
    public event Action? Changed;

    /// <summary>"Test" pressed — the main window fires a sample alert.</summary>
    public event Action? TestRequested;

    public void Load()
    {
        var s = AttentionSettingsStore.Load(_path);
        Enabled = s.Enabled;
        OnBlocked = s.OnBlocked;
        OnDone = s.OnDone;
        OnNotify = s.OnNotify;
        OnlyWhenInactive = s.OnlyWhenInactive;
        FlashWindow = s.FlashWindow;
        Popup = s.Popup;
        PopupSeconds = s.ResolvePopupSeconds();
    }

    public AttentionSettings Build() => new()
    {
        Enabled = Enabled,
        OnBlocked = OnBlocked,
        OnDone = OnDone,
        OnNotify = OnNotify,
        OnlyWhenInactive = OnlyWhenInactive,
        FlashWindow = FlashWindow,
        Popup = Popup,
        PopupSeconds = Math.Clamp((int)(PopupSeconds ?? 8), AttentionSettings.MinPopupSeconds, AttentionSettings.MaxPopupSeconds),
    };

    [RelayCommand]
    private void Save()
    {
        try
        {
            var s = Build();
            AttentionSettingsStore.Save(s, _path);
            PopupSeconds = s.PopupSeconds;
            Status = s.Enabled ? "Saved." : "Saved — notifications are off.";
            AppLogger.Log($"[Settings] attention saved | enabled={s.Enabled} blocked={s.OnBlocked} done={s.OnDone} notify={s.OnNotify} inactiveOnly={s.OnlyWhenInactive} flash={s.FlashWindow} popup={s.Popup}/{s.PopupSeconds}s");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Status = "Save failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void Test()
    {
        Status = "Test alert sent (the flash only shows while another app has the focus).";
        TestRequested?.Invoke();
    }
}
