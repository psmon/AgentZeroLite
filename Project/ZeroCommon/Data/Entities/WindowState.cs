namespace Agent.Common.Data.Entities;

public class AppWindowState
{
    public int Id { get; set; } = 1;
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 860;
    public double Height { get; set; } = 720;
    public bool IsMaximized { get; set; }
    public int LastActiveGroupIndex { get; set; }
    public int LastActiveTabIndex { get; set; }

    /// <summary>
    /// Version for which user clicked "don't show onboarding again".
    /// When current app version differs from this, onboarding auto-shows.
    /// Empty string = onboarding has never been dismissed.
    /// </summary>
    public string OnboardingDismissedVersion { get; set; } = "";

    /// <summary>
    /// Whether the bot window is "sticky-docked" to the main window's right edge.
    /// Persists across sessions so the user's preference is remembered.
    /// </summary>
    public bool IsBotDocked { get; set; } = true;
}
