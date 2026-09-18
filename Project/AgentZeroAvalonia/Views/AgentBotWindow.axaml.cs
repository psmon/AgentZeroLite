using Avalonia.Controls;

namespace AgentZeroAvalonia.Views;

/// <summary>
/// The bot as its own window (M0041). It holds no state — its <c>DataContext</c> is the same
/// <see cref="ViewModels.AgentBotViewModel"/> the docked pane uses, so the transcript, the
/// mode and a running turn all survive the move. Closing it re-docks rather than losing the
/// bot, which is the WPF <c>EmbedBotFromBot</c> contract.
/// </summary>
public partial class AgentBotWindow : Window
{
    /// <summary>Set while the shell itself is closing the window, so it does not re-dock.</summary>
    public bool ClosingProgrammatically { get; set; }

    /// <summary>Raised when the user closed the window and the bot should return to the shell.</summary>
    public event Action? EmbedRequested;

    public AgentBotWindow()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (ClosingProgrammatically) return;
            e.Cancel = true;          // keep the instance; the shell decides what happens next
            EmbedRequested?.Invoke();
        };
    }
}
