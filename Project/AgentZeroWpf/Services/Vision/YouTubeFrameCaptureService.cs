using System.IO;
using System.Threading;
using System.Windows.Threading;
using AgentZeroWpf.Module;
using AgentZeroWpf.Services.Browser;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AgentZeroWpf.Services.Vision;

/// <summary>
/// Plays a YouTube video inside a WebView2 and grabs the visible frame on a timer
/// via <see cref="CoreWebView2.CapturePreviewAsync"/> — the pip-free capture path
/// chosen for the vision test (no yt-dlp/FFmpeg native binaries). Each captured PNG
/// is raised on <see cref="FrameCaptured"/>; the caller single-flights the (heavier)
/// inference so slow interpretation drops frames instead of queuing them.
///
/// The player is NOT a bare <c>youtube.com/embed/{id}</c> navigation — that gives
/// WebView2 a null origin, which YouTube rejects for many videos with error 153
/// ("동영상 플레이어 구성 오류"). Instead we serve a tiny page over a real https
/// origin (a WebView2 virtual-host mapping) and create the player through the
/// YouTube IFrame Player API, which supplies a valid <c>origin</c> and plays every
/// embeddable video. A dedicated user-data folder under %TEMP% is used so an
/// installed (read-only Program Files) build can still create its WebView2 env.
///
/// Only the visible viewport is captured, so the hosting <see cref="WebView2"/>
/// must stay rendered (not collapsed) while a test runs.
/// </summary>
public sealed class YouTubeFrameCaptureService
{
    private const string VirtualHost = "az-ytplayer.invalid";

    private static readonly string WebRoot =
        Path.Combine(Path.GetTempPath(), "AgentZeroLite_Vision", "web");
    private static readonly string UserDataRoot =
        Path.Combine(Path.GetTempPath(), "AgentZeroLite_Vision", "udf");

    private readonly WebView2 _webview;
    private DispatcherTimer? _timer;
    private int _captureInFlight; // 0/1 interlocked — one CapturePreview at a time
    private bool _ready;
    private bool _vhostMapped;

    public event Action<byte[]>? FrameCaptured;
    public event Action<string>? Status;

    public YouTubeFrameCaptureService(WebView2 webview) => _webview = webview;

    /// <summary>
    /// Extract a YouTube video id from the common URL shapes (watch?v=, youtu.be/,
    /// /embed/, /shorts/, /live/) or a bare id. Delegates to the existing, tested
    /// <see cref="WebDevHost.ParseYouTubeId"/> so the two YouTube surfaces agree.
    /// </summary>
    public static bool TryParseVideoId(string url, out string id)
    {
        id = WebDevHost.ParseYouTubeId(url) ?? "";
        return id.Length > 0;
    }

    /// <summary>
    /// Load the muted-autoplay IFrame player for <paramref name="url"/> and start
    /// the capture timer. Throws if the URL yields no video id.
    /// </summary>
    public async Task StartAsync(string url, int intervalMs)
    {
        if (!TryParseVideoId(url, out var id))
            throw new ArgumentException("Could not parse a YouTube video id from that URL.");

        Directory.CreateDirectory(WebRoot);
        Directory.CreateDirectory(UserDataRoot);
        File.WriteAllText(Path.Combine(WebRoot, "player.html"), PlayerHtml);

        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: UserDataRoot);
        await _webview.EnsureCoreWebView2Async(env);

        if (!_vhostMapped)
        {
            _webview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHost, WebRoot, CoreWebView2HostResourceAccessKind.Allow);
            _vhostMapped = true;
        }

        _ready = false;
        _webview.CoreWebView2.ContentLoading -= OnContentLoading;
        _webview.CoreWebView2.ContentLoading += OnContentLoading;
        _webview.CoreWebView2.WebMessageReceived -= OnWebMessage;
        _webview.CoreWebView2.WebMessageReceived += OnWebMessage;

        // Real https origin → the IFrame API accepts it (no error 153).
        _webview.CoreWebView2.Navigate($"https://{VirtualHost}/player.html?v={id}");

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(400, intervalMs)) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void Stop()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
        _ready = false;
        if (_webview.CoreWebView2 is not null)
        {
            _webview.CoreWebView2.ContentLoading -= OnContentLoading;
            _webview.CoreWebView2.WebMessageReceived -= OnWebMessage;
            try { _webview.CoreWebView2.Navigate("about:blank"); } catch { }
        }
    }

    private void OnContentLoading(object? sender, CoreWebView2ContentLoadingEventArgs e) => _ready = true;

    // The player page posts "yterror:<code>" when the IFrame API raises onError.
    // 101/150 = owner disabled embedding (nothing we can do — tell the user);
    // 100 = removed/private; 2 = bad id. Surface it instead of capturing dead frames.
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg;
        try { msg = e.TryGetWebMessageAsString(); }
        catch { return; }
        if (string.IsNullOrEmpty(msg) || !msg.StartsWith("yterror:")) return;

        var code = msg["yterror:".Length..];
        var hint = code is "101" or "150"
            ? "this video's owner disabled embedding — try another link"
            : code is "100" ? "video is private or removed"
            : code is "2" ? "invalid video id"
            : "playback error";
        Status?.Invoke($"YouTube error {code} — {hint}");
        AppLogger.Log($"[Vision] YouTube player error {code}");
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        // CapturePreviewAsync fails before the first ContentLoading fires.
        if (!_ready || _webview.CoreWebView2 is null) return;
        if (Interlocked.CompareExchange(ref _captureInFlight, 1, 0) != 0) return;

        try
        {
            using var ms = new MemoryStream();
            await _webview.CoreWebView2.CapturePreviewAsync(
                CoreWebView2CapturePreviewImageFormat.Png, ms);
            FrameCaptured?.Invoke(ms.ToArray());
        }
        catch (Exception ex)
        {
            Status?.Invoke($"capture error: {ex.Message}");
            AppLogger.Log($"[Vision] frame capture failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _captureInFlight, 0);
        }
    }

    // Minimal IFrame Player API page. The id comes in via ?v=; origin is the
    // virtual host, which is what fixes YouTube error 153. Muted autoplay +
    // no controls keeps the captured frame clean for detection.
    private const string PlayerHtml = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<style>html,body{margin:0;padding:0;background:#000;overflow:hidden}#p{position:fixed;inset:0}</style>
</head>
<body>
<div id="p"></div>
<script src="https://www.youtube.com/iframe_api"></script>
<script>
function qp(k){return new URLSearchParams(location.search).get(k)||'';}
var player;
function onYouTubeIframeAPIReady(){
  player=new YT.Player('p',{
    videoId:qp('v'),
    host:'https://www.youtube.com',
    playerVars:{autoplay:1,mute:1,controls:0,rel:0,playsinline:1,modestbranding:1,fs:0,disablekb:1,origin:location.origin},
    events:{
      onReady:function(e){try{e.target.mute();e.target.playVideo();}catch(_){}},
      onError:function(e){try{window.chrome.webview.postMessage('yterror:'+e.data);}catch(_){}}
    }
  });
}
</script>
</body>
</html>
""";
}
