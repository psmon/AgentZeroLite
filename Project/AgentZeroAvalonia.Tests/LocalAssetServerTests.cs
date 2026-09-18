using System.Net;
using AgentZeroAvalonia.Services;
using Xunit;

namespace AgentZeroAvalonia.Tests;

public class LocalAssetServerTests : IDisposable
{
    private readonly string _root;

    public LocalAssetServerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "az-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "vendor"));
        File.WriteAllText(Path.Combine(_root, "index.html"), "<!doctype html><title>t</title>");
        File.WriteAllText(Path.Combine(_root, "vendor", "x.js"), "console.log(1)");
        File.WriteAllText(Path.Combine(_root, "secret.exe"), "MZ");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "az-outside-" + Path.GetFileName(_root) + ".html"), "outside");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Resolve_enforces_token_whitelist_and_root()
    {
        const string token = "abc123";
        Assert.Equal(LocalAssetServer.Outcome.Ok, LocalAssetServer.Resolve(_root, token, "/abc123/index.html?webgl=1", out var path, out var ct));
        Assert.EndsWith("index.html", path);
        Assert.StartsWith("text/html", ct);

        Assert.Equal(LocalAssetServer.Outcome.Ok, LocalAssetServer.Resolve(_root, token, "/abc123/", out _, out _));
        Assert.Equal(LocalAssetServer.Outcome.Ok, LocalAssetServer.Resolve(_root, token, "/abc123/vendor/x.js", out _, out var js));
        Assert.StartsWith("text/javascript", js);

        Assert.Equal(LocalAssetServer.Outcome.BadToken, LocalAssetServer.Resolve(_root, token, "/wrong/index.html", out _, out _));
        Assert.Equal(LocalAssetServer.Outcome.BadToken, LocalAssetServer.Resolve(_root, token, "/index.html", out _, out _));
        Assert.Equal(LocalAssetServer.Outcome.Forbidden, LocalAssetServer.Resolve(_root, token, "/abc123/../index.html", out _, out _));
        Assert.Equal(LocalAssetServer.Outcome.Forbidden, LocalAssetServer.Resolve(_root, token, "/abc123/%2e%2e/index.html", out _, out _));
        Assert.Equal(LocalAssetServer.Outcome.Forbidden, LocalAssetServer.Resolve(_root, token, "/abc123/secret.exe", out _, out _));
        Assert.Equal(LocalAssetServer.Outcome.NotFound, LocalAssetServer.Resolve(_root, token, "/abc123/missing.js", out _, out _));
    }

    [Fact]
    public async Task Serves_over_loopback_with_no_store_and_refuses_bad_tokens()
    {
        using var server = new LocalAssetServer(_root);
        using var http = new HttpClient();

        var ok = await http.GetAsync(server.UrlFor("index.html", "webgl=1"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("text/html", ok.Content.Headers.ContentType?.MediaType);
        Assert.True(ok.Headers.CacheControl?.NoStore);
        Assert.Contains("<title>t</title>", await ok.Content.ReadAsStringAsync());

        var js = await http.GetAsync(server.UrlFor("vendor/x.js"));
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);

        var missing = await http.GetAsync(server.UrlFor("nope.css"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var badToken = await http.GetAsync($"http://127.0.0.1:{server.Port}/not-the-token/index.html");
        Assert.Equal(HttpStatusCode.Forbidden, badToken.StatusCode);

        var exe = await http.GetAsync(server.UrlFor("secret.exe"));
        Assert.Equal(HttpStatusCode.Forbidden, exe.StatusCode);
    }
}
