using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Agent.Common;
using Agent.Common.Data;
using Agent.Common.Platform;
using AgentZeroAvalonia.Actors;
using AgentZeroAvalonia.Cli;
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

            var vm = new MainWindowViewModel();
            vm.LoadState();

            var router = new CliCommandRouter(desktop) { Groups = () => vm.Groups };
            _cliServer = CliServer.Start(router);

            desktop.MainWindow = new MainWindow { DataContext = vm };

            desktop.ShutdownRequested += (_, _) =>
            {
                try { _cliServer?.Dispose(); } catch { }
                ActorSystemManager.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
