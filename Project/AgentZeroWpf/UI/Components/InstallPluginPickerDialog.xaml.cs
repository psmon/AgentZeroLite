using System.Windows;
using System.Windows.Controls;
using AgentZeroWpf.Services.Browser;

namespace AgentZeroWpf.UI.Components;

public partial class InstallPluginPickerDialog : Window
{
    public enum InstallMode { None, Zip, Git }

    public InstallMode Mode { get; private set; } = InstallMode.None;
    public string? GitUrl { get; private set; }

    private readonly List<OfficialPluginEntry> _official = new();

    public InstallPluginPickerDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadOfficialAsync();
    }

    // ─── Official plugins ─────────────────────────────────────────────

    private async Task LoadOfficialAsync()
    {
        cbOfficial.IsEnabled = false;
        btnReloadOfficial.IsEnabled = false;
        btnInstallOfficial.IsEnabled = false;
        OfficialMetaPanel.Visibility = Visibility.Collapsed;
        tbOfficialStatus.Text = "Loading the catalogue from GitHub…";

        var entries = await OfficialPluginCatalog.DiscoverAsync();
        _official.Clear();
        _official.AddRange(entries);

        cbOfficial.Items.Clear();
        foreach (var e in _official)
        {
            var icon = string.IsNullOrEmpty(e.Icon) ? "•" : e.Icon;
            var version = string.IsNullOrEmpty(e.Version) ? "" : $"  v{e.Version}";
            cbOfficial.Items.Add(new ComboBoxItem
            {
                Content = $"{icon}  {e.Name}{version}   —   {e.Id}",
                Tag = e,
            });
        }

        if (_official.Count == 0)
        {
            tbOfficialStatus.Text = "Couldn't reach github.com (offline?) or the official folder is empty. " +
                                    "You can still paste a custom URL below.";
        }
        else
        {
            tbOfficialStatus.Text = $"{_official.Count} official plugin(s) discovered. Pick one to view details.";
            cbOfficial.IsEnabled = true;
            cbOfficial.SelectedIndex = 0;
        }
        btnReloadOfficial.IsEnabled = true;
    }

    private void OnOfficialChanged(object sender, SelectionChangedEventArgs e)
    {
        if (cbOfficial.SelectedItem is not ComboBoxItem item || item.Tag is not OfficialPluginEntry entry)
        {
            OfficialMetaPanel.Visibility = Visibility.Collapsed;
            btnInstallOfficial.IsEnabled = false;
            return;
        }
        tbOfficialIcon.Text    = string.IsNullOrEmpty(entry.Icon) ? "•" : entry.Icon;
        tbOfficialName.Text    = entry.Name;
        tbOfficialVersion.Text = string.IsNullOrEmpty(entry.Version) ? "" : $"v{entry.Version}";
        tbOfficialId.Text      = $"id: {entry.Id}";
        tbOfficialDesc.Text    = string.IsNullOrWhiteSpace(entry.Description)
            ? "(no description provided in manifest)"
            : entry.Description;
        tbOfficialUrl.Text     = entry.GitFolderUrl;
        OfficialMetaPanel.Visibility = Visibility.Visible;
        btnInstallOfficial.IsEnabled = true;
    }

    private async void OnReloadOfficial(object sender, RoutedEventArgs e)
    {
        await LoadOfficialAsync();
    }

    private void OnInstallOfficial(object sender, RoutedEventArgs e)
    {
        if (cbOfficial.SelectedItem is not ComboBoxItem item || item.Tag is not OfficialPluginEntry entry)
            return;
        Mode = InstallMode.Git;
        GitUrl = entry.GitFolderUrl;
        DialogResult = true;
    }

    // ─── Custom (third-party) ─────────────────────────────────────────

    private void OnPickZip(object sender, RoutedEventArgs e)
    {
        // ZIP path stays a single click — caller handles the OpenFileDialog.
        Mode = InstallMode.Zip;
        DialogResult = true;
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
        Mode = InstallMode.Git;
        GitUrl = url;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
