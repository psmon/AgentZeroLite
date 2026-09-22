using System.Diagnostics;
using AgentOne.Actors;
using AgentOne.Agent;
using AgentOne.Llm;
using AgentOne.Services;

namespace AgentOne.Commands;

/// <summary>
/// The background session: one <see cref="ChatSession"/> in a detached
/// process, reachable through a local pipe. `start` spawns it, `ask` talks
/// to it, `status` reads it, `stop` ends it. One at a time, so "the session"
/// always means the same thing to the person and to the other agents driving
/// it. `selftest` runs a server and a client in one process over a private
/// pipe — that is how chat mode is exercised on a machine with no terminal.
/// </summary>
public sealed class SessionCommand
{
    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        var rest = args.Length > 0 ? args[1..] : [];

        return sub switch
        {
            "-h" or "--help" => Help(),
            "start" => await StartAsync(rest, ct),
            "serve" => await ServeAsync(rest, ct),
            "stop" => await StopAsync(ct),
            "status" => await StatusAsync(ct),
            "selftest" => await SelfTestAsync(ct),
            _ => Unknown(sub)
        };
    }

    // ------------------------------------------------------------- start

    private static async Task<int> StartAsync(string[] args, CancellationToken ct)
    {
        if (!AgentOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine($"agent-one session start: {error}");
            return 2;
        }

        if (SessionRegistry.LoadAlive() is { } running)
        {
            Console.Error.WriteLine($"agent-one session: one is already running (pid {running.Pid}, root {running.Root}) — `agent-one session stop` first");
            return 1;
        }

        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            Console.Error.WriteLine("agent-one session start: cannot find my own executable to spawn");
            return 1;
        }

        // The child gets the same options, verbatim, and runs `serve` — detached,
        // sharing no handle with this process (see DetachedProcess for the 3m55s
        // pipe stall that inheriting them caused).
        Process child;
        try
        {
            child = DetachedProcess.Start(exe, ["session", "serve", .. args], options.Root);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine("agent-one session start: could not spawn the server: " + ex.Message);
            return 1;
        }

        // Wait for the server to write its record; it does so once the pipe is up.
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(100, ct);
            if (SessionRegistry.Load() is { } record && record.Pid == child.Id)
            {
                Console.WriteLine($"background session started (pid {record.Pid}) · root {record.Root} · {(record.Smart ? "smart" : "basic")}");
                Console.WriteLine($"  ask it:   agent-one ask \"<request>\"     stop it: agent-one session stop");
                Console.WriteLine($"  log:      {ServerLogPath}");
                return 0;
            }
            if (child.HasExited)
            {
                Console.Error.WriteLine($"agent-one session start: the server exited at once (code {child.ExitCode}) — see {ServerLogPath}");
                return 1;
            }
        }

        Console.Error.WriteLine("agent-one session start: the server did not come up in 10 s — see " + ServerLogPath);
        return 1;
    }

    private static string ServerLogPath => Path.Combine(AppPaths.EnsureLogDir(), "session.log");

    // ------------------------------------------------------------- serve

    /// <summary>The server process itself. Never run this by hand; `start` does.</summary>
    private static async Task<int> ServeAsync(string[] args, CancellationToken ct)
    {
        if (!AgentOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine($"agent-one session serve: {error}");
            return 2;
        }

        // A detached process has no console worth writing to; everything goes
        // to the log, and the file is what `start` points a person at on failure.
        var log = new StreamWriter(ServerLogPath, append: true) { AutoFlush = true };
        Console.SetOut(log);
        Console.SetError(log);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] session serve · pid {Environment.ProcessId} · root {options.Root}");

        IAgentSession session;
        try
        {
            session = AgentGateway.Start(options.Config, options.Root, streaming: true);
        }
        catch (ChatProviderException ex)
        {
            Console.Error.WriteLine("session serve: " + ex.Message);
            return 2;
        }

        using (session)
        {
            var pipe = SessionRegistry.PipeName();
            var server = new SessionServer(session, pipe);

            SessionRegistry.Save(new SessionRecord
            {
                Pid = Environment.ProcessId,
                Pipe = pipe,
                Root = options.Root,
                Started = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                Smart = session.Smart
            });

            try
            {
                await server.RunAsync(ct);
            }
            finally
            {
                if (SessionRegistry.Load()?.Pid == Environment.ProcessId) SessionRegistry.Clear();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] session serve · stopped");
            }
        }

        return 0;
    }

    // ------------------------------------------------------- stop / status

    private static async Task<int> StopAsync(CancellationToken ct)
    {
        var record = SessionRegistry.LoadAlive();
        if (record is null)
        {
            Console.WriteLine("no background session is running");
            return 0;
        }

        var reply = await SessionClient.SendAsync(record.Pipe, new PipeRequest { Op = "stop" }, null, null, ct);
        if (reply.Event == "result")
        {
            // Give it a moment to exit on its own; only then use force.
            for (var i = 0; i < 30 && SessionRegistry.IsAlive(record.Pid); i++) await Task.Delay(100, ct);
        }

        if (SessionRegistry.IsAlive(record.Pid))
        {
            try { using var p = Process.GetProcessById(record.Pid); p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { /* gone */ }
            Console.WriteLine($"background session (pid {record.Pid}) did not stop by itself — killed");
        }
        else
        {
            Console.WriteLine($"background session (pid {record.Pid}) stopped");
        }

        SessionRegistry.Clear();
        return 0;
    }

    private static async Task<int> StatusAsync(CancellationToken ct)
    {
        var record = SessionRegistry.LoadAlive();
        if (record is null)
        {
            Console.WriteLine("no background session is running  ·  agent-one session start [--smart] [-r <root>]");
            return 1;
        }

        Console.WriteLine($"background session · pid {record.Pid} · since {record.Started} · pipe {record.Pipe}");
        var reply = await SessionClient.SendAsync(record.Pipe, new PipeRequest { Op = "status" }, null, null, ct);
        if (reply.Event == "result")
            foreach (var line in reply.Text.Split('\n')) Console.WriteLine("  " + line);
        else
            Console.WriteLine("  " + reply.Text);
        return 0;
    }

    // ---------------------------------------------------------- selftest

    /// <summary>
    /// Server and client in one process, over a private pipe, on the echo
    /// provider: a turn goes in, its events and result come back, a status
    /// reads, a stop ends the loop. Chat mode, proven with no terminal.
    /// </summary>
    public static async Task<int> SelfTestAsync(CancellationToken ct)
    {
        var failures = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "agent-one-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        var config = new AgentConfig();
        config.TrySet("saveSessions", "false", out _);
        var pipe = SessionRegistry.PipeName("-selftest-" + Guid.NewGuid().ToString("N")[..6]);

        try
        {
            using var session = AgentGateway.Start(config, root, streaming: true);
            var server = new SessionServer(session, pipe);
            var serving = server.RunAsync(ct);
            await server.Listening.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            var events = new List<PipeEvent>();
            var result = await SessionClient.SendAsync(pipe,
                new PipeRequest { Op = "ask", Text = "hello from the selftest" },
                e => { events.Add(e); return Task.CompletedTask; }, null, ct);

            if (result.Event != "result") failures.Add($"ask did not end in a result: {result.Event} {result.Text}");
            else if (result.Text != "hello from the selftest") failures.Add($"echo came back as '{result.Text}'");
            if (!events.Any(e => e.Event == "activity")) failures.Add("no activity event was streamed");
            if (!events.Any(e => e.Event == "step" && e.Tool == "final")) failures.Add("no final step event was streamed");

            var status = await SessionClient.SendAsync(pipe, new PipeRequest { Op = "status" }, null, null, ct);
            if (status.Event != "result" || !status.Text.Contains("turns 1")) failures.Add($"status did not count the turn: {status.Text}");

            var stop = await SessionClient.SendAsync(pipe, new PipeRequest { Op = "stop" }, null, null, ct);
            if (stop.Event != "result") failures.Add($"stop was not acknowledged: {stop.Text}");

            await serving.WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (!server.StopRequested) failures.Add("the server did not record the stop");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            failures.Add("the pipe round-trip failed: " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }

        if (failures.Count > 0)
        {
            Console.Error.WriteLine("agent-one session selftest: FAILED");
            foreach (var failure in failures) Console.Error.WriteLine("  - " + failure);
            return 1;
        }

        Console.WriteLine("agent-one session selftest: ok (server + client over a pipe · ask, events, status, stop)");
        return 0;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"agent-one session: unknown subcommand '{sub}'");
        PrintHelp();
        return 2;
    }

    private static int Help()
    {
        PrintHelp();
        return 0;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            agent-one session start [options]   Start the one background session (detached), same options as `chat`
            agent-one session status            Is one running, and its status block
            agent-one session stop              End it
            agent-one session selftest          Server + client in-process over a private pipe, echo provider

            agent-one ask "<request>" [--yes] [--json] [-q]
                                                Send one request to the background session and print the
                                                turn as the REPL would. A command needing approval is asked
                                                here (or approved with --yes); a design choice is asked here
                                                (or takes the recommendation when not interactive).

            One session at a time. It belongs to the workspace it was started in
            (-r/--root), keeps its conversation between asks, and writes the same
            session log and workspace memory the chat window does. Slash commands
            work through ask: /status, /new, /reset.

            Why: chat mode can be exercised with no terminal (the selftest, CI), and
            another agent can drive this one from a script.
            """);
    }
}
