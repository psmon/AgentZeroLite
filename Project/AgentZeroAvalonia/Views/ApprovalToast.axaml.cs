using Avalonia.Controls;

namespace AgentZeroAvalonia.Views;

/// <summary>
/// The approval overlay (M0041). All behaviour lives in
/// <see cref="ViewModels.ApprovalToastViewModel"/> so the countdown and the mute window are
/// testable headlessly; this is the shell around it.
/// </summary>
public partial class ApprovalToast : UserControl
{
    public ApprovalToast() => InitializeComponent();
}
