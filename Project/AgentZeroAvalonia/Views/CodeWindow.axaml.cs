using Avalonia;
using Avalonia.Controls;

namespace AgentZeroAvalonia.Views;

/// <summary>
/// The code view in its own window. Like <see cref="AgentBotWindow"/> it holds no state — the
/// tree, the open file and its scroll come from the shared <see cref="ViewModels.CodeViewModel"/>
/// — and closing it docks the view back into the bottom panel rather than closing it.
///
/// It is a side window, as a detached terminal or plugin is: shown without an owner (owned, it
/// stayed above the main window and read as a modal dialog), with 📌 for always-on-top and
/// ↩ Dock back in its strip. Where it was is kept for the rest of the run, so floating it again
/// puts it back on the same spot.
/// </summary>
public partial class CodeWindow : Window
{
    // Device-independent, like PluginWindow's remembered rect; per process, not stored.
    private static PixelPoint? s_position;
    private static Size? s_size;
    private static bool s_topmost;

    /// <summary>Set while the shell itself closes the window, so it does not re-dock.</summary>
    public bool ClosingProgrammatically { get; set; }

    /// <summary>The user wants the view back in the bottom panel (strip button or the close box).</summary>
    public event Action? EmbedRequested;

    public CodeWindow()
    {
        InitializeComponent();
        if (s_size is { } size)
        {
            Width = size.Width;
            Height = size.Height;
        }
        Topmost = s_topmost;
        PinButton.IsChecked = s_topmost;
        PinButton.IsCheckedChanged += (_, _) => Topmost = s_topmost = PinButton.IsChecked == true;
        DockBackButton.Click += (_, _) => EmbedRequested?.Invoke();

        Closing += (_, e) =>
        {
            Remember();
            if (ClosingProgrammatically || e.CloseReason is WindowCloseReason.OwnerWindowClosing
                                                        or WindowCloseReason.ApplicationShutdown
                                                        or WindowCloseReason.OSShutdown)
                return;
            e.Cancel = true;
            EmbedRequested?.Invoke();
        };
    }

    /// <summary>
    /// Where this run last left the window if that is still on a screen, else centred over
    /// <paramref name="anchor"/> (an unowned window cannot use CenterOwner). Call before <c>Show</c>.
    /// </summary>
    public void PlaceNear(Window anchor)
    {
        if (s_position is { } p && anchor.Screens.All.Any(s => s.WorkingArea.Contains(p)))
        {
            Position = p;
            return;
        }
        var scale = anchor.DesktopScaling;
        var x = anchor.Position.X + (int)Math.Round((anchor.Bounds.Width - Width) / 2 * scale);
        var y = anchor.Position.Y + (int)Math.Round((anchor.Bounds.Height - Height) / 2 * scale);
        Position = new PixelPoint(Math.Max(anchor.Position.X, x), Math.Max(anchor.Position.Y, y));
    }

    private void Remember()
    {
        if (WindowState != WindowState.Normal) return;
        s_position = Position;
        s_size = ClientSize;
    }
}
