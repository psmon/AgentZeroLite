using Agent.Common;
using Agent.Common.Module;
using Agent.Common.Remote;
using Agent.Common.Services;
using AgentZeroAvalonia.Services.Remote;
using AgentZeroAvalonia.Terminal;
using Avalonia.Threading;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// The Remote page's wiring — compiled only when <c>IncludeRemote</c> is true (the download
/// builds). The server reaches the terminals through <see cref="GuiTerminals"/>, which marshals
/// every read to the UI thread, like the CLI router does.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The Remote page; null only if it could not be built.</summary>
    public RemoteViewModel? Remote { get; private set; }

    partial void InitRemote()
    {
        try
        {
            var settings = RemoteSettingsStore.Load();
            var auth = new RemoteAuthService(settings, () => RemoteSettingsStore.Save(settings));
            var server = new RemoteServer(auth, new GuiTerminals(this),
                Path.Combine(AppContext.BaseDirectory, "Wasm", "remote"));
            Remote = new RemoteViewModel(server, settings);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[Remote] page init failed", ex);
        }
    }

    partial void RemotePageShown(bool visible) => Remote?.SetVisible(visible);

    /// <summary><c>-cli remote status|start|stop|pin|open</c> (and WPF's <c>remote-pin</c>).</summary>
    public string RemoteCommand(string verb)
    {
        if (Remote is null) return CliIpcProtocolError("the Remote page failed to start — see the app log");
        if (verb != "open") return Remote.CliCommand(verb);
        Page = AppPage.Remote;
        return Remote.CliCommand("status");
    }

    private static string CliIpcProtocolError(string message) => Agent.Common.Platform.CliIpcProtocol.ErrorJson(message);

    private sealed class GuiTerminals(MainWindowViewModel vm) : IRemoteTerminals
    {
        public string CatalogJson() => OnUi(() => TerminalCatalogJson.BuildTerminalListJson(vm.Groups, null));

        public ITerminalSession? Resolve(int group, int tab) => OnUi(() =>
        {
            var groups = vm.Groups;
            if (group < 0 || group >= groups.Count) return null;
            var tabs = groups[group].TabsView;
            return tab >= 0 && tab < tabs.Count ? tabs[tab].Session : null;
        });

        public (int Group, int Tab)? Active() => OnUi<(int, int)?>(() =>
        {
            var groups = vm.Groups;
            // The terminal in front, when its workspace is a top-level one (terminal-list's indices)…
            if (vm.ActiveWorkspace is { } ws)
            {
                for (var g = 0; g < groups.Count; g++)
                    if (ReferenceEquals(groups[g], ws) && groups[g].TabsView.Count > ws.ActiveTabIndex && groups[g].TabsView[ws.ActiveTabIndex].Session is not null)
                        return (g, ws.ActiveTabIndex);
            }
            // …else the first terminal that is running.
            for (var g = 0; g < groups.Count; g++)
                for (var t = 0; t < groups[g].TabsView.Count; t++)
                    if (groups[g].TabsView[t].Session is not null) return (g, t);
            return null;
        });

        public (int Cols, int Rows)? SizeOf(ITerminalSession session) => TerminalGeometry.Get(session);

        private static T OnUi<T>(Func<T> read)
            => Dispatcher.UIThread.CheckAccess() ? read() : Dispatcher.UIThread.Invoke(read);
    }
}
