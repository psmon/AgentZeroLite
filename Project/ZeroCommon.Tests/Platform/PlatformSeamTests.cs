using System.IO;
using System.Text.Json;
using Agent.Common.Platform;
using Agent.Common.Security;

namespace ZeroCommon.Tests.Platform;

/// <summary>M0033 — the seams the Avalonia host stands on: paths, single instance, secrets.</summary>
[Trait("Category", "Platform")]
public sealed class PlatformSeamTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "aztest-platform-" + Guid.NewGuid().ToString("n"));
    private readonly IDisposable _root;

    public PlatformSeamTests()
    {
        Directory.CreateDirectory(_scratch);
        _root = AppPaths.OverrideRoot(_scratch);
    }

    public void Dispose()
    {
        _root.Dispose();
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    [Fact]
    public void AppPaths_resolve_under_the_root_and_create_folders()
    {
        Assert.Equal(_scratch, AppPaths.DataRoot);
        Assert.Equal(Path.Combine(_scratch, "x.json"), AppPaths.File("x.json"));
        Assert.True(Directory.Exists(AppPaths.LogsDir));
        Assert.StartsWith(_scratch, AppPaths.ModelsDir);
    }

    [Fact]
    public void FileLockGuard_second_acquire_fails_until_the_first_is_released()
    {
        using var first = new FileLockGuard("t", _scratch);
        Assert.True(first.TryAcquire());
        Assert.True(first.TryAcquire());   // idempotent for the owner

        using (var second = new FileLockGuard("t", _scratch))
            Assert.False(second.TryAcquire());

        first.Dispose();
        using var third = new FileLockGuard("t", _scratch);
        Assert.True(third.TryAcquire());
    }

    [Fact]
    public void SingleInstanceGuard_factory_picks_an_implementation_for_this_os()
    {
        using var guard = SingleInstanceGuard.Create("aztest-" + Guid.NewGuid().ToString("n"));
        Assert.True(guard.TryAcquire());
    }

    [Fact]
    public void AesGcm_protector_round_trips_is_idempotent_and_passes_legacy_plaintext()
    {
        var p = new AesGcmFileSecretProtector(Path.Combine(_scratch, "k.key"));
        var token = p.Protect("sk-secret-값");
        Assert.StartsWith(AesGcmFileSecretProtector.Marker, token);
        Assert.Equal(token, p.Protect(token));
        Assert.Equal("sk-secret-값", p.Unprotect(token));
        Assert.Equal("plain", p.Unprotect("plain"));
        Assert.Equal("", p.Protect(""));

        // A second instance over the same key file reads what the first wrote.
        var again = new AesGcmFileSecretProtector(Path.Combine(_scratch, "k.key"));
        Assert.Equal("sk-secret-값", again.Unprotect(token));
    }

    [Fact]
    public void AesGcm_protector_returns_null_on_tamper_or_wrong_key()
    {
        var p = new AesGcmFileSecretProtector(Path.Combine(_scratch, "k1.key"));
        var token = p.Protect("hello");
        var tampered = token[..^4] + "AAAA";
        Assert.Null(p.Unprotect(tampered));
        Assert.Null(p.Unprotect(AesGcmFileSecretProtector.Marker + "!!notbase64"));

        var other = new AesGcmFileSecretProtector(Path.Combine(_scratch, "k2.key"));
        Assert.Null(other.Unprotect(token));
    }

    [Fact]
    public async Task CliIpcBridge_round_trips_json_over_a_named_pipe()
    {
        var pipe = "aztest-" + Guid.NewGuid().ToString("n");
        var bridge = CliIpcBridge.Create(pipe);
        using var server = bridge.StartServer((req, _) =>
        {
            using var doc = JsonDocument.Parse(req);
            var cmd = doc.RootElement.GetProperty("command").GetString();
            return Task.FromResult($"{{\"ok\":true,\"echo\":\"{cmd}\"}}");
        });

        var reply = await bridge.SendRequestAsync("{\"command\":\"status\"}", 5000, CancellationToken.None);
        Assert.NotNull(reply);
        Assert.Equal("status", JsonDocument.Parse(reply!).RootElement.GetProperty("echo").GetString());

        // Two callers at once — the second must not wait for the first's handler.
        var slowPipe = "aztest-" + Guid.NewGuid().ToString("n");
        var slow = CliIpcBridge.Create(slowPipe);
        using var slowServer = slow.StartServer(async (req, ct) =>
        {
            if (req.Contains("slow")) await Task.Delay(800, ct);
            return "{\"ok\":true}";
        });
        var t1 = slow.SendRequestAsync("{\"command\":\"slow\"}", 5000, CancellationToken.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t2 = await slow.SendRequestAsync("{\"command\":\"fast\"}", 5000, CancellationToken.None);
        Assert.NotNull(t2);
        Assert.True(sw.ElapsedMilliseconds < 700, $"fast call waited {sw.ElapsedMilliseconds} ms behind the slow one");
        Assert.NotNull(await t1);
    }

    [Fact]
    public async Task CliIpcBridge_reports_no_server_and_handler_errors_as_envelopes()
    {
        var bridge = CliIpcBridge.Create("aztest-nobody-" + Guid.NewGuid().ToString("n"));
        Assert.Null(await bridge.SendRequestAsync("{\"command\":\"status\"}", 300, CancellationToken.None));

        var pipe = "aztest-" + Guid.NewGuid().ToString("n");
        var throwing = CliIpcBridge.Create(pipe);
        using var server = throwing.StartServer((_, _) => throw new InvalidOperationException("boom"));
        var reply = await throwing.SendRequestAsync("{\"command\":\"x\"}", 5000, CancellationToken.None);
        Assert.NotNull(reply);
        var root = JsonDocument.Parse(reply!).RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("boom", root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CliIpcBridge_refuses_oversized_requests()
    {
        var pipe = "aztest-" + Guid.NewGuid().ToString("n");
        var bridge = CliIpcBridge.Create(pipe);
        using var server = bridge.StartServer((_, _) => Task.FromResult("{\"ok\":true}"));
        var huge = "{\"command\":\"" + new string('x', CliIpcProtocol.MaxMessageBytes + 10) + "\"}";
        var reply = await bridge.SendRequestAsync(huge, 5000, CancellationToken.None);
        Assert.NotNull(reply);
        Assert.False(JsonDocument.Parse(reply!).RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void CliIpcBridge_server_stops_promptly_on_dispose()
    {
        var bridge = CliIpcBridge.Create("aztest-" + Guid.NewGuid().ToString("n"));
        var server = bridge.StartServer((_, _) => Task.FromResult("{}"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        server.Dispose();
        Assert.True(sw.ElapsedMilliseconds < 1500);
    }
}
