using Avalonia;
using Agent.Common;
using Agent.Common.Platform;
using Agent.Common.Security;
using AgentZeroAvalonia.Cli;
using AgentZeroAvalonia.Security;

namespace AgentZeroAvalonia;

/// <summary>
/// Entry point (M0034). One executable, two modes, exactly like the WPF host:
/// <c>-cli</c> anywhere in the arguments runs the console client and never starts
/// Avalonia; anything else is the GUI, guarded to a single instance per user session.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Credentials at rest are protected for both modes — the CLI's `web`/`bot-chat`
        // paths load settings too — so the protector is chosen before the fork.
        SecretProtection.Protector = SecretProtectorFactory.Create();

        if (args.Any(a => a.Equals("-cli", StringComparison.OrdinalIgnoreCase)))
            return CliMain.Run(args);

        using var guard = SingleInstanceGuard.Create();
        if (!guard.TryAcquire())
        {
            // Same name as the WPF host's mutex on Windows: the two GUIs share one
            // database and must not run at once either.
            Console.Error.WriteLine("AgentZero Lite is already running.");
            return 1;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[CRASH] Avalonia lifetime", ex);
            throw;
        }
    }

    /// <summary>The designer/previewer entry — keep the signature.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
