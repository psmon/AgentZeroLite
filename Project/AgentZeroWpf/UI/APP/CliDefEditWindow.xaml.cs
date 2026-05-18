using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Agent.Common.Module;
using AgentZeroWpf.Security;

namespace AgentZeroWpf.UI.APP;

public partial class CliDefEditWindow : Window
{
    private static readonly Dictionary<string, string> _presetMap = new()
    {
        ["cmd.exe"] = "CMD",
        ["powershell.exe"] = "PowerShell5",
        ["pwsh.exe"] = "PowerShell7",
    };

    public CliDefinition? Result { get; private set; }

    // M0021: when editing an existing definition with a password, the operator
    // can leave the PasswordBox empty to KEEP the encrypted blob as-is. This
    // flag holds the original ciphertext for the save path.
    private string? _existingEncryptedPassword;

    public CliDefEditWindow(CliDefinition? existing = null)
    {
        InitializeComponent();
        Loaded += (_, _) => ThemeHelper.ApplyDarkTitleBar(this);

        if (existing is not null)
        {
            tbName.Text = existing.Name;
            tbArgs.Text = existing.Arguments ?? "";
            SetExePreset(existing.ExePath);

            // Remote section — populate from existing record.
            cbRemote.IsChecked = existing.IsRemote;
            tbSshHost.Text = existing.SshHost ?? "";
            tbSshUser.Text = existing.SshUser ?? "";
            tbSshKeyPath.Text = existing.SshKeyPath ?? "";
            _existingEncryptedPassword = existing.EncryptedPassword;
            var authMode = SshCommandBuilder.ParseAuthMethod(existing.SshAuthMethod, SshAuthMode.PublicKey);
            if (authMode == SshAuthMode.Password)
                rbAuthPassword.IsChecked = true;
            else
                rbAuthPublicKey.IsChecked = true;

            if (existing.IsRemote)
                gridRemoteDetails.Visibility = Visibility.Visible;
            UpdateAuthMethodPanels();
            if (!string.IsNullOrEmpty(_existingEncryptedPassword))
                tbPasswordHint.Text = "Password stored (encrypted). Leave blank to keep, or type a new one to replace.";
        }
        else
        {
            // 신규: 기본 선택 CMD, Args 기본값
            cbExePreset.SelectedIndex = 0;
            tbArgs.Text = "-NoExit -Command";
        }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnWindowStateChanged(object? sender, EventArgs e)
        => MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "" : "";

    private void SetExePreset(string exePath)
    {
        for (int i = 0; i < cbExePreset.Items.Count; i++)
        {
            if (cbExePreset.Items[i] is ComboBoxItem item &&
                item.Tag is string tag &&
                string.Equals(tag, exePath, StringComparison.OrdinalIgnoreCase))
            {
                cbExePreset.SelectedIndex = i;
                return;
            }
        }

        // Custom
        cbExePreset.SelectedIndex = cbExePreset.Items.Count - 1;
        tbExePath.Text = exePath;
        tbExePath.IsEnabled = true;
    }

    private void OnExePresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (cbExePreset.SelectedItem is not ComboBoxItem item) return;
        string tag = item.Tag as string ?? "";

        if (string.IsNullOrEmpty(tag))
        {
            // Custom
            tbExePath.IsEnabled = true;
            tbExePath.Focus();
        }
        else
        {
            tbExePath.Text = tag;
            tbExePath.IsEnabled = false;
        }
    }

    // ── M0021 Remote section handlers ──

    private void OnRemoteToggled(object sender, RoutedEventArgs e)
    {
        if (gridRemoteDetails is null) return;
        gridRemoteDetails.Visibility = (cbRemote.IsChecked == true)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnAuthMethodChanged(object sender, RoutedEventArgs e)
        => UpdateAuthMethodPanels();

    private void UpdateAuthMethodPanels()
    {
        // Sub-panels share grid rows; toggle Visibility so the row collapses
        // when not needed, keeping the dialog compact.
        if (lblKeyPath is null || gridKeyPath is null || lblPassword is null || panelPassword is null) return;
        bool isPassword = rbAuthPassword.IsChecked == true;
        lblKeyPath.Visibility = isPassword ? Visibility.Collapsed : Visibility.Visible;
        gridKeyPath.Visibility = isPassword ? Visibility.Collapsed : Visibility.Visible;
        lblPassword.Visibility = isPassword ? Visibility.Visible : Visibility.Collapsed;
        panelPassword.Visibility = isPassword ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBrowseSshKey(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select SSH private key (.pem)",
            Filter = "PEM files (*.pem)|*.pem|Key files (*.key)|*.key|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true)
            tbSshKeyPath.Text = dlg.FileName;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(tbName.Text) || string.IsNullOrWhiteSpace(tbExePath.Text))
        {
            System.Windows.MessageBox.Show("Name and ExePath are required.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool isRemote = cbRemote.IsChecked == true;
        string? sshHost = null;
        string? sshUser = null;
        string? sshAuthMethod = null;
        string? sshKeyPath = null;
        string? encryptedPassword = null;

        if (isRemote)
        {
            sshHost = tbSshHost.Text?.Trim();
            sshUser = tbSshUser.Text?.Trim();
            if (string.IsNullOrWhiteSpace(sshHost) || string.IsNullOrWhiteSpace(sshUser))
            {
                System.Windows.MessageBox.Show(
                    "Host and User are required when Remote is enabled.",
                    "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool isPasswordAuth = rbAuthPassword.IsChecked == true;
            sshAuthMethod = isPasswordAuth
                ? SshCommandBuilder.AuthMethodPassword
                : SshCommandBuilder.AuthMethodPublicKey;

            if (isPasswordAuth)
            {
                var typed = pbSshPassword.Password;
                if (!string.IsNullOrEmpty(typed))
                {
                    encryptedPassword = DpapiSecretProtector.Protect(typed);
                    // Clear the PasswordBox immediately — defense in depth.
                    pbSshPassword.Clear();
                }
                else
                {
                    // Empty PasswordBox + we're editing: keep existing ciphertext.
                    encryptedPassword = _existingEncryptedPassword;
                    if (string.IsNullOrEmpty(encryptedPassword))
                    {
                        System.Windows.MessageBox.Show(
                            "Password is required for password authentication.",
                            "Validation Error",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
            }
            else
            {
                sshKeyPath = tbSshKeyPath.Text?.Trim();
                if (string.IsNullOrWhiteSpace(sshKeyPath))
                {
                    System.Windows.MessageBox.Show(
                        "Private key path is required for public-key authentication.",
                        "Validation Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
        }

        Result = new CliDefinition
        {
            Name = tbName.Text.Trim(),
            ExePath = tbExePath.Text.Trim(),
            Arguments = string.IsNullOrWhiteSpace(tbArgs.Text) ? null : tbArgs.Text.Trim(),
            IsRemote = isRemote,
            SshHost = sshHost,
            SshUser = sshUser,
            SshAuthMethod = sshAuthMethod,
            SshKeyPath = sshKeyPath,
            EncryptedPassword = encryptedPassword,
        };
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
