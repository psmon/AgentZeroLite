using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AgentZeroWpf.Services;

/// <summary>
/// Renders Mermaid diagram text to SVG using a hidden WebView2 + bundled mermaid.min.js.
/// Call InitAsync() once, then RenderAsync() per diagram.
/// </summary>
public sealed class MermaidRenderer : IDisposable
{
    private WebView2? _webView;
    private bool _ready;
    private string? _mermaidJs;
    private readonly Window _host;

    public MermaidRenderer()
    {
        // Hidden host window for WebView2 (never shown)
        _host = new Window
        {
            Width = 1, Height = 1,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Opacity = 0,
        };
    }

    public async Task InitAsync()
    {
        if (_ready) return;

        _mermaidJs ??= LoadEmbeddedMermaidJs();

        _host.Show();
        _host.Hide();

        _webView = new WebView2();
        _host.Content = _webView;

        var env = await CoreWebView2Environment.CreateAsync(
            userDataFolder: Path.Combine(Path.GetTempPath(), "AgentZeroLite_Mermaid"));
        await _webView.EnsureCoreWebView2Async(env);

        // Load a blank page, inject mermaid.js
        _webView.CoreWebView2.NavigateToString("<html><body></body></html>");
        await WaitForNavigationAsync();

        await _webView.CoreWebView2.ExecuteScriptAsync(_mermaidJs);
        await _webView.CoreWebView2.ExecuteScriptAsync(
            "mermaid.initialize({ startOnLoad: false, theme: 'dark' });");

        _ready = true;
    }

    /// <summary>Returns SVG string for the given mermaid diagram text, or null on error.</summary>
    public async Task<string?> RenderAsync(string mermaidText)
    {
        if (!_ready || _webView == null) return null;

        try
        {
            var escaped = mermaidText
                .Replace("\\", "\\\\")
                .Replace("`", "\\`")
                .Replace("$", "\\$")
                .Replace("\r", "")
                .Replace("\n", "\\n");

            var script = $@"
                (async () => {{
                    try {{
                        const {{ svg }} = await mermaid.render('m' + Date.now(), `{escaped}`);
                        return svg;
                    }} catch(e) {{
                        return '__ERR__' + e.message;
                    }}
                }})()";

            var result = await _webView.CoreWebView2.ExecuteScriptAsync(script);

            // Result is JSON-encoded string (with quotes)
            if (result.StartsWith('"') && result.EndsWith('"'))
            {
                result = System.Text.Json.JsonSerializer.Deserialize<string>(result);
            }

            if (result == null || result.StartsWith("__ERR__")) return null;
            return result;
        }
        catch
        {
            return null;
        }
    }

    private Task WaitForNavigationAsync()
    {
        var tcs = new TaskCompletionSource();
        _webView!.CoreWebView2.NavigationCompleted += OnNav;
        return tcs.Task;

        void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            _webView.CoreWebView2.NavigationCompleted -= OnNav;
            tcs.TrySetResult();
        }
    }

    private static string LoadEmbeddedMermaidJs()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("mermaid.min.js")
            ?? throw new FileNotFoundException("Embedded mermaid.min.js not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        _webView?.Dispose();
        _host.Close();
    }
}
