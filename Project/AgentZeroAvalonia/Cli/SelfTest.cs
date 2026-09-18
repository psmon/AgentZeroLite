using System.Text;
using System.Text.Json;
using Agent.Common.Platform;
using Agent.Common.Security;
using AgentZeroAvalonia.Services;

namespace AgentZeroAvalonia.Cli;

/// <summary>
/// <c>-cli selftest pty|ipc|secrets|all</c> — what a CI runner without a display can still
/// prove. Each prints one line per check and exits non-zero on the first failure.
/// The pty check spawns the real backend for this OS (ConPTY on Windows, Porta.Pty on
/// macOS/Linux) and waits for a marker to come back through it.
/// </summary>
internal static class SelfTest
{
    public static int Run(string[] args)
    {
        var which = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
        if (which is not ("ipc" or "secrets" or "pty" or "all"))
        {
            Console.Error.WriteLine("Usage: selftest pty|ipc|secrets|all");
            return 2;
        }
        var failures = 0;
        if (which is "ipc" or "all") failures += Check("ipc", Ipc);
        if (which is "secrets" or "all") failures += Check("secrets", Secrets);
        if (which is "pty" or "all") failures += Check("pty", Pty);
        return failures == 0 ? 0 : 1;
    }

    private static int Check(string name, Func<string> body)
    {
        try
        {
            Console.WriteLine($"[selftest/{name}] ok — {body()}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[selftest/{name}] FAIL — {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static string Ipc()
    {
        var pipe = "AgentZeroLite.selftest." + Environment.ProcessId;
        var bridge = CliIpcBridge.Create(pipe);
        using var server = bridge.StartServer((req, _) => Task.FromResult("{\"ok\":true,\"echo\":" + req + "}"));
        var reply = bridge.SendRequest("{\"command\":\"ping\"}", 5000)
                    ?? throw new InvalidOperationException("no reply over the pipe");
        using var doc = JsonDocument.Parse(reply);
        if (!doc.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(reply);
        return $"named pipe round-trip on '{pipe}'";
    }

    private static string Secrets()
    {
        var protector = SecretProtection.Protector;
        var token = protector.Protect("selftest-secret");
        if (token == "selftest-secret") throw new InvalidOperationException($"{protector.GetType().Name} stored plaintext");
        if (protector.Unprotect(token) != "selftest-secret") throw new InvalidOperationException("round-trip mismatch");
        return protector.GetType().Name;
    }

    private static string Pty()
    {
        if (!PtyHostFactory.IsSupported) throw new PlatformNotSupportedException($"no PTY backend for {Environment.OSVersion}");
        var marker = "AZ_PTY_" + Environment.ProcessId;
        var spec = PtyHostFactory.EchoSpec(marker);
        var seen = new ManualResetEventSlim();
        var sb = new StringBuilder();
        using var host = PtyHostFactory.Start(spec, 80, 24);
        host.Output += s =>
        {
            lock (sb)
            {
                sb.Append(s);
                if (sb.ToString().Contains(marker, StringComparison.Ordinal)) seen.Set();
            }
        };
        if (!seen.Wait(10_000))
        {
            string got;
            lock (sb) got = sb.ToString();
            throw new TimeoutException($"'{marker}' not echoed within 10 s | {host.Diagnostics} | got {got.Length} chars");
        }
        return $"{PtyHostFactory.BackendName} echoed the marker | {host.Diagnostics}";
    }
}
