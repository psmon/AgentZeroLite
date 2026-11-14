using Agent.Common.Agents;
using AgentZeroAvalonia.Layout;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// The command palette's items (M0042): every terminal and workspace to jump to, a new terminal
/// of each definition, every chord's command, every page and settings section, and the code
/// panel's faces — built when the palette opens, so it lists what exists now.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Ctrl+Shift+P (Cmd on macOS), or the palette entry in a menu: the view opens the window.</summary>
    public event Action? PaletteRequested;

    public void OpenPalette() => PaletteRequested?.Invoke();

    public IReadOnlyList<PaletteItem> BuildPaletteItems()
    {
        var items = new List<PaletteItem>();
        var isMac = OperatingSystem.IsMacOS();

        // Terminals first: going to one is what the palette is used for most.
        for (int g = 0; g < Workspaces.Count; g++)
        {
            var ws = Workspaces[g];
            for (int t = 0; t < ws.Tabs.Count; t++)
            {
                var tab = ws.Tabs[t];
                var state = _agentTracker.Lookup(g, t);
                var hint = state?.Activity switch
                {
                    AgentActivity.Blocked => "blocked",
                    AgentActivity.Working => "working",
                    AgentActivity.Done => state.Seen ? "done" : "done · new",
                    _ => tab.IsTerminalStarted ? "running" : "not started",
                };
                items.Add(new PaletteItem("Terminal", $"{ws.DisplayName} / {tab.Title}", hint, () =>
                {
                    Page = AppPage.Terminals;
                    SelectTab(tab);
                }));
            }
        }
        foreach (var ws in Workspaces)
            items.Add(new PaletteItem("Workspace", ws.DisplayName, ws.DirectoryPath ?? "", () =>
            {
                Page = AppPage.Terminals;
                ActiveWorkspace = ws;
            }));
        foreach (var def in CliDefinitions)
            items.Add(new PaletteItem("New terminal", def.Name, ActiveWorkspace?.DisplayName ?? "", () =>
            {
                Page = AppPage.Terminals;
                NewTerminal(def);
            }));

        // Chord commands, with their keys as the hint.
        var effective = Hotkeys.ToDictionary(h => h.Name, h => string.Join("+",
            new[] { h.Ctrl ? "Ctrl" : null, h.Alt ? "Alt" : null, h.Shift ? "Shift" : null, h.Meta ? (isMac ? "Cmd" : "Win") : null, h.Key }
                .Where(p => p is not null)));
        foreach (var (id, description) in HotkeyTable.Commands)
        {
            // PanelToggle and BotToggle both show / hide the bot: list it once.
            if (id is HotkeyTable.PaletteOpen or Agent.Common.Services.WindowCommandIds.PanelToggle) continue;
            var captured = id;
            items.Add(new PaletteItem("Command", description, effective.GetValueOrDefault(id, ""), () => HandleHotkey(captured)));
        }

        // The code panel's faces.
        items.Add(new PaletteItem("Code", "Files of the active workspace", "", () => { CodeVisible = true; SelectCodeTabCommand.Execute(null); }));
        items.Add(new PaletteItem("Code", "Git log", "", () => { CodeVisible = true; SelectGitLogTabCommand.Execute(null); }));
        items.Add(new PaletteItem("Code", "Diff: working tree", "", () => { CodeVisible = true; Code.Diff.Source = DiffSource.WorkingTree; SelectDiffTabCommand.Execute(null); }));
        items.Add(new PaletteItem("Code", "Diff: AI changes", Code.Diff.AiChangeCount > 0 ? $"{Code.Diff.AiChangeCount} file(s)" : "",
            () => { CodeVisible = true; Code.Diff.Source = DiffSource.AiChanges; SelectDiffTabCommand.Execute(null); }));

        // Pages and settings sections.
        items.Add(new PaletteItem("Go to", "Terminals", "", () => Page = AppPage.Terminals));
        items.Add(new PaletteItem("Go to", "Plugins", "", () => Page = AppPage.Plugins));
        if (Wearable is not null) items.Add(new PaletteItem("Go to", "Wearable", "", () => Page = AppPage.Wearable));
        if (RemoteSupported) items.Add(new PaletteItem("Go to", "Remote", "", () => Page = AppPage.Remote));
        foreach (var section in Enum.GetValues<SettingsSection>())
        {
            var s = section;
            items.Add(new PaletteItem("Settings", s switch
            {
                SettingsSection.Llm => "LLM",
                SettingsSection.Mcp => "MCP servers",
                SettingsSection.Cli => "CLI definitions",
                _ => s.ToString(),
            }, "", () => { Settings.Section = s; Page = AppPage.Settings; }));
        }
        items.Add(new PaletteItem("Notifications", "Send a test alert", "", TestAttention));
        return items;
    }
}
