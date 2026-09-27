using System.Diagnostics;
using System.Net.Sockets;
using AgentOne.Dashboard;
using AgentOne.Services;

namespace AgentOne.Commands;

/// <summary>
/// <c>agent-one dashboard</c> — a local web page over what agent-one left
/// behind: every workspace's memory, sessions and knowledge graph, with a
/// Cypher box. Runs in the foreground until Ctrl+C; the printed link carries
/// the token the page needs.
/// </summary>
public sealed class DashboardCommand
{
    public const int DefaultPort = 8790;

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        int? port = null;
        var open = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help": PrintHelp(); return 0;
                case "-p" or "--port":
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var p) || p is < 0 or > 65535)
                    {
                        Console.Error.WriteLine("agent-one dashboard: --port needs a number (0 = any free port)");
                        return 2;
                    }
                    port = p;
                    i++;
                    break;
                case "--open": open = true; break;
                default:
                    Console.Error.WriteLine($"agent-one dashboard: unknown option '{args[i]}'");
                    return 2;
            }
        }

        DashboardServer server;
        try
        {
            server = new DashboardServer(new DashboardData(), port ?? DefaultPort);
        }
        catch (SocketException) when (port is null)
        {
            // The default is taken (another dashboard, most likely): any free port will do.
            server = new DashboardServer(new DashboardData(), 0);
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"agent-one dashboard: cannot listen on 127.0.0.1:{port} — {ex.Message}");
            return 1;
        }

        using (server)
        {
            Console.WriteLine($"agent-one dashboard · {AppPaths.WorkspacesDir}");
            Console.WriteLine();
            Console.WriteLine($"  {server.Url}");
            Console.WriteLine();
            Console.WriteLine("  read-only · 127.0.0.1 only · the link carries this run's token");
            Console.WriteLine("  Ctrl+C to stop");

            if (open) OpenBrowser(server.Url);

            try { await server.RunAsync(ct); }
            catch (OperationCanceledException) { }
        }

        Console.WriteLine("agent-one dashboard: stopped");
        return 0;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine("  (could not open a browser — click or copy the link)");
        }
    }

    public static void PrintHelp()
    {
        Console.WriteLine($"""
            agent-one dashboard — look at what agent-one has left behind, in a local web page

            Usage:
              agent-one dashboard [--port <n>] [--open]

            Options:
              -p, --port <n>   Port on 127.0.0.1 (default {DefaultPort}; taken → any free port; 0 = any free port)
                  --open       Open the page in the default browser as well

            What it shows, per workspace or across all of them:
              Overview   every workspace under ~/.agent-one/workspaces: memory size, sessions, graph counts
              Memory     memory.md, one card per turn (asked · tools · outcome), searchable
              Sessions   the transcripts: prompts, tool steps, decisions, results
              Graph      the Kùzu knowledge graph drawn as nodes and edges; click one for its properties
              Cypher     any query, against one graph or all of them (a "workspace" column is added)

            It only reads. Graphs are opened read-only, so a CREATE or DELETE is refused by Kùzu itself,
            and each is opened per request and closed again. A graph that a running chat or background
            session holds is shown as busy until that session ends — Kùzu allows one writer per database.

            The server binds 127.0.0.1 only, and the API answers only requests that carry the token in the
            printed link; stop it with Ctrl+C.
            """);
    }
}
