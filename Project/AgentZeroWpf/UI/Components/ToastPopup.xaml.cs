using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AgentZeroWpf.UI.Components;

public partial class ToastPopup : UserControl
{
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public ToastPopup()
    {
        InitializeComponent();
        toastBorder.Opacity = 0;
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            toastBorder.Opacity = 0;
        };
    }

    public void Show(string message, string icon = "\uE8C8")
    {
        txtMessage.Text = message;
        txtIcon.Text = icon;

        _hideTimer.Stop();
        toastBorder.Opacity = 1;
        _hideTimer.Start();
    }
}
