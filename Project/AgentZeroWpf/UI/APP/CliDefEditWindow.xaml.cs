using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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

    public CliDefEditWindow(CliDefinition? existing = null)
    {
        InitializeComponent();
        Loaded += (_, _) => ThemeHelper.ApplyDarkTitleBar(this);

        if (existing is not null)
        {
            tbName.Text = existing.Name;
            tbArgs.Text = existing.Arguments ?? "";
            SetExePreset(existing.ExePath);
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
        => MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

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

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(tbName.Text) || string.IsNullOrWhiteSpace(tbExePath.Text))
        {
            System.Windows.MessageBox.Show("Name and ExePath are required.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new CliDefinition
        {
            Name = tbName.Text.Trim(),
            ExePath = tbExePath.Text.Trim(),
            Arguments = string.IsNullOrWhiteSpace(tbArgs.Text) ? null : tbArgs.Text.Trim(),
        };
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
