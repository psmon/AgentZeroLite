using Agent.Common.Telemetry;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Agent.Common;
using Agent.Common.Data;
using Agent.Common.Llm;
using Agent.Common.Platform;
using AgentZeroAvalonia.Actors;
using AgentZeroAvalonia.Cli;
using AgentZeroAvalonia.Services;
using AgentZeroAvalonia.ViewModels;
using AgentZeroAvalonia.Views;

namespace AgentZeroAvalonia;

/// <summary>
/// Boot order (M0034): log file → database → actor system on the UI thread → CLI pipe
/// server → main window. Shutdown is fire-and-forget through Akka's coordinated
/// shutdown (<c>exit-clr = on</c>), exactly as the WPF host does, because awaiting it on
/// the UI thread deadlocks the synchronized dispatcher.
/// </summary>
public partial class App : Application
{
    private CliServer? _cliServer;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try { AppLogger.EnableFileOutput(AppPaths.DataRoot); } catch { }
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) AppLogger.LogError("[CRASH] AppDomain", ex);
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                AppLogger.LogError("[CRASH] UnobservedTask", e.Exception);
                e.SetObserved();
            };
            AppLogger.Log($"[App] AgentZero Lite (Avalonia) starting on {Environment.OSVersion} / {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");

            try
            {
                AppDbContext.InitializeDatabase();
            }
            catch (Exception ex)
            {
                AppLogger.LogError("[DB] InitializeDatabase failed", ex);
            }

            // The HOCON synchronized-dispatcher captures SynchronizationContext.Current at
            // Initialize(); Avalonia installs its own on the UI thread, but assert rather
            // than assume — a null here would be actors posting to nowhere.
            if (SynchronizationContext.Current is null)
                SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
            ActorSystemManager.Initialize();
            StartTelemetryCollectors();

            var vm = new MainWindowViewModel();
            vm.LoadState();

            // M0041: URL bubbles open in the OS browser.
            vm.Bot.OpenUrl = url =>
            {
                try { desktop.MainWindow?.Launcher.LaunchUriAsync(new Uri(url)); }
                catch (Exception ex) { AppLogger.Log($"[Bot] open url failed: {ex.GetType().Name}: {ex.Message}"); }
            };

            // Store policy 11.16: every AI answer can be reported — mail client or browser.
            vm.Bot.Report.OpenUri = vm.Bot.OpenUrl;
            vm.Bot.Report.ModelName = () =>
            {
                var s = LlmSettingsStore.Load();
                return s.ActiveBackend == LlmActiveBackend.External
                    ? $"{s.External.Provider} · {s.ResolveExternalModel()}"
                    : $"local · {s.ModelId}";
            };

            var router = new CliCommandRouter(desktop)
            {
                Groups = () => vm.Groups,
                ExecuteWindowCommand = vm.HandleHotkey,
                WebSurface = WorkspaceToolHost.SharedWeb,
                BotAsk = text =>
                {
                    vm.BotVisible = true;
                    vm.Bot.AttachActors();
                    vm.Bot.AskAi(text);
                },
                BotChat = (from, message) =>
                {
                    vm.Bot.AttachActors();
                    vm.Bot.ReceiveExternalChat(from, message);
                },
                PluginCommand = root => PluginVerbs.HandleAsync(root, vm),
                ActivateWorkspace = name =>
                {
                    var ws = vm.Workspaces.FirstOrDefault(w => string.Equals(w.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                             ?? (int.TryParse(name, out var i) && i >= 0 && i < vm.Workspaces.Count ? vm.Workspaces[i] : null);
                    if (ws is null) return $"no workspace '{name}'";
                    vm.Page = AppPage.Terminals;
                    vm.ActiveWorkspace = ws;
                    return null;
                },
                AgentState = () => Dispatcher.UIThread.InvokeAsync(vm.AgentStateJson).GetTask(),
                LayoutStatus = () => (vm.ActiveWorkspace?.DockLayoutJson, vm.ActiveWorkspace?.Layout.PaneCount ?? 0, vm.ActiveWorkspace?.DisplayName),
                FloatingCount = () => vm.FloatingCount,
                CodeCommand = vm.CodeCommandAsync,
                WorktreeCommand = vm.WorktreeCommandAsync,
                AttentionCommand = verb => Dispatcher.UIThread.InvokeAsync(() => vm.AttentionCommand(verb)).GetTask(),
            };
            _cliServer = CliServer.Start(router);

            // M0041: the bot can now open a second window. Without this the default
            // (OnLastWindowClose) would keep the process alive after the shell is closed
            // while the floating bot is still up.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow { DataContext = vm };
            // Models switched on in "load at startup" (all off by default) load on the pool after the window is
            // up — starting the app never waits for one; everything else loads on first use.
            desktop.MainWindow.Opened += (_, _) =>
                _ = Agent.Common.Models.ModelResidency.PreloadAtStartupAsync(m => AppLogger.Log(m));
            vm.Wearable?.AutoStart();
            vm.StartAgentStateMonitor();
            vm.StartWorktreeMonitor();
            vm.StartCostMonitor();

            desktop.ShutdownRequested += (_, _) =>
            {
                try { vm.StopAgentStateMonitor(); } catch { }
                try { vm.FlushPersist(); } catch { }
                try { vm.Bot.Detach(); } catch { }
                try { vm.Wearable?.Shutdown(); } catch { }
                try { _cliServer?.Dispose(); } catch { }
                StopTelemetryCollectors();
                // stdio MCP servers are child processes; end them with the app. Off the UI
                // thread: waiting here on continuations that post back to it would deadlock
                // (the same trap ActorSystemManager.Shutdown documents).
                try { Task.Run(() => Agent.Common.Mcp.McpHub.Shared.ResetAsync()).Wait(TimeSpan.FromSeconds(3)); } catch { }
                ActorSystemManager.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The three telemetry collectors the WPF host starts in <c>App.OnStartup</c>. The Plugins
    /// page's token-monitor / token-remaining widgets read what they store; without them the
    /// widgets showed only what a manual Refresh collected. Each start is independent, as in WPF.
    /// </summary>
    private static void StartTelemetryCollectors()
    {
        try { TokenUsageCollector.Instance.Start(); }
        catch (Exception ex) { AppLogger.LogError("[TokenCollector] start failed", ex); }
        // Keep the statusLine wrapper script in step with this build (idempotent overwrite).
        try { StatusLineWrapperInstaller.EnsureWrapperOnDisk(); }
        catch (Exception ex) { AppLogger.LogError("[StatusLineWrapper] EnsureWrapperOnDisk failed", ex); }
        try { TokenRemainingCollector.Instance.Start(); }
        catch (Exception ex) { AppLogger.LogError("[TokenRemainingCollector] start failed", ex); }
        try { SessionHeartbeatCollector.Instance.Start(); }
        catch (Exception ex) { AppLogger.LogError("[SessionHeartbeatCollector] start failed", ex); }
    }

    private static void StopTelemetryCollectors()
    {
        try { TokenUsageCollector.Instance.Stop(); } catch { }
        try { TokenRemainingCollector.Instance.Stop(); } catch { }
        try { SessionHeartbeatCollector.Instance.Stop(); } catch { }
    }
}
