using System.Windows;

namespace AgentZeroWpf.UI.Components;

public partial class InstallPluginPickerDialog : Window
{
    public enum InstallMode { None, Zip, Git }

    public InstallMode Mode { get; private set; } = InstallMode.None;
    public string? GitUrl { get; private set; }

    public InstallPluginPickerDialog() => InitializeComponent();

    private void OnPickZip(object sender, RoutedEventArgs e)
    {
        // ZIP path stays a single click — caller handles the OpenFileDialog.
        Mode = InstallMode.Zip;
        DialogResult = true;
    }

    private void OnPickGit(object sender, RoutedEventArgs e)
    {
        Mode = InstallMode.Git;
        ModePicker.Visibility = Visibility.Collapsed;
        GitInput.Visibility = Visibility.Visible;
        btnConfirmGit.Visibility = Visibility.Visible;
        tbGitUrl.SelectAll();
        tbGitUrl.Focus();
    }

    private void OnConfirmGit(object sender, RoutedEventArgs e)
    {
        var url = tbGitUrl.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(this, "Enter a Git folder URL.", "Install Plugin",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        GitUrl = url;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
