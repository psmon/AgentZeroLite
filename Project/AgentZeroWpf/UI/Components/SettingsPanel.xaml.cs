using System.IO;
using System.Windows;
using System.Windows.Controls;
using AgentZeroWpf.Module;
using AgentZeroWpf.UI.APP;
using Microsoft.EntityFrameworkCore;

namespace AgentZeroWpf.UI.Components;

public partial class SettingsPanel : UserControl
{
    public event Action? CliDefinitionsChanged;
    public event Action? OnboardingDismissed;

    public SettingsPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshList();
            RefreshPathStatus();
            InitializeLlmTab();
            InitializeVoiceTab();
            InitializeDiarizationTab();
            InitializeMusicTab();
            InitializeVisionTab();
        };
    }

    public void ShowOnboardingTab()
    {
    }

    private void RefreshList()
    {
        try
        {
            using var db = new AppDbContext();
            lvCliDefs.ItemsSource = db.CliDefinitions.OrderBy(d => d.SortOrder).ToList();
        }
        catch { }
    }


    private void RefreshPathStatus()
    {
        string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        tbCurrentPath.Text = appDir;

        string current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        bool registered = current
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(p => string.Equals(p.TrimEnd(Path.DirectorySeparatorChar), appDir, StringComparison.OrdinalIgnoreCase));

        if (registered)
        {
            lblPathStatus.Text = "PATH Registered";
            lblPathStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#00FFF0")!);
            btnRegisterPath.Content = "Registered";
            btnRegisterPath.IsEnabled = false;
        }
        else
        {
            lblPathStatus.Text = "Not Registered — Click to register";
            lblPathStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF2D95")!);
            btnRegisterPath.Content = "Register PATH";
            btnRegisterPath.IsEnabled = true;
        }
    }

    private void OnRegisterPath(object sender, RoutedEventArgs e)
    {
        string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";

        // Remove existing AgentZero paths (e.g. D:\AgentZero\v1.4.2) before adding current
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !IsAgentZeroPath(p, appDir))
            .ToList();
        parts.Add(appDir);

        string newPath = string.Join(';', parts);
        Environment.SetEnvironmentVariable("Path", newPath, EnvironmentVariableTarget.User);
        AppLogger.Log($"[PATH] 등록 완료: {appDir}");
        RefreshPathStatus();
    }

    private static bool IsAgentZeroPath(string path, string currentAppDir)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar);
        // Exact match — already registered
        if (string.Equals(trimmed, currentAppDir, StringComparison.OrdinalIgnoreCase))
            return true;

        // This cleanup recognizes ONLY this project's own binaries
        // (AgentZeroLite.exe). Sibling projects manage their own PATH:
        //   - AgentWin (upstream) ships AgentZeroWpf.exe
        //   - Any third-party "AgentZero" tool is also out of scope
        // Refusing to recognize them prevents this Register-PATH button
        // from silently deleting a co-installed sibling project's entry.
        if (File.Exists(Path.Combine(trimmed, "AgentZeroLite.exe")))
            return true;

        // Stale registration whose directory has been deleted on disk.
        // Matches only when the leaf name still looks like an AgentZero*
        // build/release folder AND doesn't carry the Wpf/Win markers that
        // signal the sibling project (AgentWin / AgentZeroWpf.*).
        if (!Directory.Exists(trimmed))
        {
            var leaf = Path.GetFileName(trimmed);
            if (leaf.StartsWith("AgentZero", StringComparison.OrdinalIgnoreCase)
                && !leaf.Contains("Wpf", StringComparison.OrdinalIgnoreCase)
                && !leaf.Contains("Win", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void OnCliDefSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = lvCliDefs.SelectedItem as CliDefinition;
        btnEditCliDef.IsEnabled = selected is not null;
        btnDelCliDef.IsEnabled = selected is not null && !selected.IsBuiltIn;
    }

    private void OnAddCliDef(object sender, RoutedEventArgs e)
    {
        var dlg = new CliDefEditWindow();
        dlg.Owner = Window.GetWindow(this);
        if (dlg.ShowDialog() == true && dlg.Result is not null)
        {
            using var db = new AppDbContext();
            var maxSort = db.CliDefinitions.Any() ? db.CliDefinitions.Max(d => d.SortOrder) : -1;
            dlg.Result.SortOrder = maxSort + 1;
            db.CliDefinitions.Add(dlg.Result);
            db.SaveChanges();
            RefreshList();
            CliDefinitionsChanged?.Invoke();
        }
    }

    private void OnEditCliDef(object sender, RoutedEventArgs e)
    {
        if (lvCliDefs.SelectedItem is not CliDefinition selected) return;

        var dlg = new CliDefEditWindow(selected);
        dlg.Owner = Window.GetWindow(this);
        if (dlg.ShowDialog() == true && dlg.Result is not null)
        {
            using var db = new AppDbContext();
            var entity = db.CliDefinitions.Find(selected.Id);
            if (entity is null) return;
            entity.Name = dlg.Result.Name;
            entity.ExePath = dlg.Result.ExePath;
            entity.Arguments = dlg.Result.Arguments;
            // M0021: ssh fields — copy through. EncryptedPassword stays as-is
            // when dlg.Result.EncryptedPassword equals _existingEncryptedPassword
            // (handled inside the dialog), so this assignment is always safe.
            entity.IsRemote = dlg.Result.IsRemote;
            entity.SshHost = dlg.Result.SshHost;
            entity.SshUser = dlg.Result.SshUser;
            entity.SshAuthMethod = dlg.Result.SshAuthMethod;
            entity.SshKeyPath = dlg.Result.SshKeyPath;
            entity.EncryptedPassword = dlg.Result.EncryptedPassword;
            db.SaveChanges();
            RefreshList();
            CliDefinitionsChanged?.Invoke();
        }
    }

    private void OnDeleteCliDef(object sender, RoutedEventArgs e)
    {
        if (lvCliDefs.SelectedItem is not CliDefinition selected || selected.IsBuiltIn) return;

        // M0021 follow-up #2: CliTab → CliDefinition FK uses OnDelete.Restrict,
        // so deleting a definition that has live tabs throws DbUpdateException.
        // Detect the usage up front, offer cascade-delete (remove tabs too),
        // and wrap SaveChanges in try/catch so any residual error surfaces as
        // a MessageBox instead of an unhandled crash.
        using var db = new AppDbContext();
        var entity = db.CliDefinitions.Find(selected.Id);
        if (entity is null) return;

        var referencingTabs = db.CliTabs
            .Where(t => t.CliDefinitionId == selected.Id)
            .ToList();

        var prompt = referencingTabs.Count == 0
            ? $"Delete CLI definition '{selected.Name}'?"
            : $"Delete CLI definition '{selected.Name}'?\n\n" +
              $"{referencingTabs.Count} tab(s) currently use this definition and will be removed as well.";
        var result = System.Windows.MessageBox.Show(
            prompt,
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            if (referencingTabs.Count > 0)
                db.CliTabs.RemoveRange(referencingTabs);
            db.CliDefinitions.Remove(entity);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Settings] Delete CLI def '{selected.Name}' failed: {ex.GetType().Name}: {ex.Message}");
            System.Windows.MessageBox.Show(
                $"Could not delete '{selected.Name}':\n\n{ex.Message}",
                "Delete Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }
        RefreshList();
        CliDefinitionsChanged?.Invoke();
    }

    private void OnSettingsCloseClick(object sender, RoutedEventArgs e)
    {
        OnboardingDismissed?.Invoke();
    }

    public void DismissOnboardingForThisVersion()
    {
    }
}
