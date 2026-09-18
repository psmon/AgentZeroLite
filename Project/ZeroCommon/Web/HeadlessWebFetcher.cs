using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Agent.Common.Web;

/// <summary>
/// Plain HTTP GET of a page for the wearable host's web tools when no GUI browser is
/// around (M0032). Only <c>http(s)</c>; loopback and link-local hosts are refused so a
/// prompt-injected "open http://127.0.0.1:…" cannot turn the watch into a probe of this
/// PC's own services. Bounded in time and bytes: a slow site costs the watch a timeout,
/// not a hang.
/// </summary>
public static class HeadlessWebFetcher
{
    public const int DefaultMaxBytes = 2_000_000;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly Regex MetaCharset = new(@"<meta\b[^>]*charset\s*=\s*[""']?\s*([\w-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        // euc-kr and friends live in the code-pages provider, which is inbox but not registered.
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 6,
        };
        var client = new HttpClient(handler) { Timeout = Timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AgentZeroLite/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,text/plain;q=0.8,*/*;q=0.5");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ko,en;q=0.8");
        return client;
    }

    public sealed record FetchResult(bool Ok, string Html, string FinalUrl, string? ContentType, string? Error);

    /// <summary>Normalises a user/model-supplied URL and applies the scheme + host policy.</summary>
    public static bool TryValidate(string? url, out Uri uri, out string error)
    {
        uri = null!;
        error = "";
        if (string.IsNullOrWhiteSpace(url))
        {
            error = "url must not be empty";
            return false;
        }
        var s = url.Trim();
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) ||
            (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
        {
            error = "only http(s) URLs can be opened";
            return false;
        }
        if (IsLocalHost(u.Host))
        {
            error = "local addresses cannot be opened from the wearable host";
            return false;
        }
        uri = u;
        return true;
    }

    public static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return true;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return false;
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.IsIPv6LinkLocal) return true;
        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes[0] == 169 && bytes[1] == 254;
    }

    public static async Task<FetchResult> FetchAsync(Uri uri, int maxBytes, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            var finalUrl = finalUri.ToString();
            var mediaType = response.Content.Headers.ContentType?.MediaType;

            // The policy in TryValidate is checked on what was asked for; a redirect chain
            // can still land on this PC (302 → http://127.0.0.1:8765/…). Apply it to where
            // we actually ended up, before a byte of the body is read.
            if (IsLocalHost(finalUri.Host))
                return new FetchResult(false, "", finalUrl, mediaType, "redirected to a local address; refused");

            if (!response.IsSuccessStatusCode)
                return new FetchResult(false, "", finalUrl, mediaType, $"HTTP {(int)response.StatusCode}");
            if (mediaType is not null &&
                !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
                !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) &&
                !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) &&
                !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                return new FetchResult(false, "", finalUrl, mediaType, $"not a text page ({mediaType})");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = await stream.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, n);
                if (buffer.Length >= maxBytes) break;
            }

            var bytes = buffer.GetBuffer();
            var length = (int)buffer.Length;
            var encoding = Encoding.UTF8;
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', ' ');
            if (string.IsNullOrEmpty(charset))
            {
                var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(length, 4096));
                var meta = MetaCharset.Match(head);
                if (meta.Success) charset = meta.Groups[1].Value;
            }
            if (!string.IsNullOrEmpty(charset))
            {
                try { encoding = Encoding.GetEncoding(charset); } catch { }
            }

            return new FetchResult(true, encoding.GetString(bytes, 0, length), finalUrl, mediaType, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FetchResult(false, "", uri.ToString(), null, $"timed out after {Timeout.TotalSeconds:0} s");
        }
        catch (Exception ex)
        {
            return new FetchResult(false, "", uri.ToString(), null, ex.Message);
        }
    }
}
