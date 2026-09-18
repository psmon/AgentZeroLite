using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Agent.Common;
using Agent.Common.Llm.Tools;
using Agent.Common.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// The Browser page (M0032): a tabbed WebView2 that is also an <see cref="IWebToolSurface"/>.
/// The wearable host reaches it through <c>-cli web …</c> (MainWindow's IPC handler calls the
/// surface methods), AgentBot's toolbelt calls them in-process, and the user drives the same
/// tabs by hand. Page text goes out through the shared <see cref="WebPageExtractor"/> so
/// what the model reads here is exactly what the headless fallback would produce.
///
/// <para>Surface methods may be called from any thread; each hops to the dispatcher. A
/// navigation is awaited through a per-tab <see cref="TaskCompletionSource{TResult}"/> that
/// <c>NavigationCompleted</c> resolves, bounded by <see cref="NavTimeout"/>.</para>
/// </summary>
public partial class BrowserPagePanel : UserControl, IWebToolSurface
{
    private sealed class BrowserTab
    {
        public int Id;
        public WebView2 View = null!;
        public Button Chip = null!;
        public string Url = "";
        public string Title = "";
        public TaskCompletionSource<bool>? NavDone;
    }

    private const int MaxTabs = 8;
    private static readonly TimeSpan NavTimeout = TimeSpan.FromSeconds(25);
    private static readonly System.Drawing.Color CanvasBg = System.Drawing.Color.FromArgb(0x0A, 0x0A, 0x14);

    private readonly List<BrowserTab> _tabs = new();
    private BrowserTab? _active;
    private int _nextId = 1;
    private Task<CoreWebView2Environment>? _envTask;
    private bool _windowClosedHooked;

    /// <summary>Raised when the user clicks the panel's close button.</summary>
    public event Action? CloseRequested;

    public BrowserPagePanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_windowClosedHooked) return;
        var window = Window.GetWindow(this);
        if (window is null) return;
        _windowClosedHooked = true;
        window.Closed += (_, _) =>
        {
            foreach (var tab in _tabs.ToArray())
            {
                try { tab.View.Dispose(); } catch { }
            }
            _tabs.Clear();
        };
    }

    // ── IWebToolSurface (any thread) ─────────────────────────────────────────

    public Task<string> SearchAsync(string query, int maxResults, CancellationToken ct)
        => OnUi(() => SearchCoreAsync(query, maxResults, ct));

    public Task<string> OpenAsync(string url, int tab, CancellationToken ct)
        => OnUi(() => OpenCoreAsync(url, tab, ct));

    public Task<string> ReadAsync(int tab, string? mode, string? find, int maxChars, CancellationToken ct)
        => OnUi(() => ReadCoreAsync(tab, mode, find, maxChars));

    public Task<string> ListTabsAsync(CancellationToken ct)
        => OnUi(() => Task.FromResult(ListTabsJson()));

    private Task<string> OnUi(Func<Task<string>> op)
    {
        if (Dispatcher.CheckAccess()) return op();
        return Dispatcher.InvokeAsync(op).Task.Unwrap();
    }

    // ── Surface implementation (UI thread) ───────────────────────────────────

    private async Task<string> OpenCoreAsync(string url, int tabId, CancellationToken ct)
    {
        // Same URL policy as the headless path: http(s) only, nothing local.
        if (!HeadlessWebFetcher.TryValidate(url, out var uri, out var error)) return ToolJson.Fail(error);

        var tab = tabId > 0 ? _tabs.FirstOrDefault(t => t.Id == tabId) : null;
        tab ??= await NewTabAsync();
        Activate(tab);

        if (!await NavigateAsync(tab, uri.ToString(), ct))
            return ToolJson.Fail($"could not open {uri} (navigation failed or timed out)");

        var content = WebPageExtractor.Extract(await OuterHtmlAsync(tab), tab.Url);
        return WebPageExtractor.ToJson(content, "summary", null, HeadlessWebToolSurface.OpenSummaryChars, tab.Url, tab.Id);
    }

    private async Task<string> SearchCoreAsync(string query, int maxResults, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return ToolJson.Fail("query must not be empty");
        var url = WebSearchParser.BuildDuckDuckGoUrl(query);

        // One search tab, reused: results pages are not worth a tab each.
        var tab = _tabs.FirstOrDefault(t => t.Url.StartsWith(WebSearchParser.DuckDuckGoHtmlEndpoint, StringComparison.OrdinalIgnoreCase));
        tab ??= await NewTabAsync();
        Activate(tab);

        if (!await NavigateAsync(tab, url, ct))
            return ToolJson.Fail("the search page did not load");

        var html = await OuterHtmlAsync(tab);
        var results = WebSearchParser.ParseDuckDuckGo(html, maxResults);
        if (results.Count == 0 && WebSearchParser.LooksBlocked(html))
            return ToolJson.Fail("the search engine refused the request (bot check); try again later");

        // The click, visibly: the first result opens in its own tab and its text rides
        // along in the reply. The search tab stays for a second look.
        System.Text.Json.Nodes.JsonObject? topPage = null;
        var top = results.FirstOrDefault();
        if (top is not null && HeadlessWebFetcher.TryValidate(top.Url, out var topUri, out _))
        {
            var pageTab = await NewTabAsync();
            Activate(pageTab);
            if (await NavigateAsync(pageTab, topUri.ToString(), ct))
            {
                var content = WebPageExtractor.Extract(await OuterHtmlAsync(pageTab), pageTab.Url);
                topPage = WebSearchParser.TopPage(content, pageTab.Url, pageTab.Id);
            }
        }
        return WebSearchParser.ToJson(query, results, topPage);
    }

    private async Task<string> ReadCoreAsync(int tabId, string? mode, string? find, int maxChars)
    {
        var tab = tabId > 0 ? _tabs.FirstOrDefault(t => t.Id == tabId) : _active;
        if (tab is null)
            return ToolJson.Fail(tabId > 0 ? $"tab {tabId} is not open" : "no page is open; call web_open first");

        var content = WebPageExtractor.Extract(await OuterHtmlAsync(tab), tab.Url);
        return WebPageExtractor.ToJson(content, mode, find,
            maxChars > 0 ? maxChars : WebPageExtractor.DefaultMaxChars, tab.Url, tab.Id);
    }

    private string ListTabsJson() => JsonSerializer.Serialize(new
    {
        ok = true,
        count = _tabs.Count,
        active = _active?.Id,
        tabs = _tabs.Select(t => new { tab = t.Id, url = t.Url, title = t.Title }).ToArray(),
    }, ToolJson.Options);

    // ── Tabs ─────────────────────────────────────────────────────────────────

    private Task<CoreWebView2Environment> EnvAsync()
        => _envTask ??= CoreWebView2Environment.CreateAsync(
            userDataFolder: Path.Combine(Path.GetTempPath(), "AgentZeroLite_Browser"));

    private async Task<BrowserTab> NewTabAsync()
    {
        if (_tabs.Count >= MaxTabs) CloseTab(_tabs[0]);

        var env = await EnvAsync();
        var view = new WebView2 { DefaultBackgroundColor = CanvasBg, Visibility = Visibility.Collapsed };
        viewHost.Children.Add(view);
        await view.EnsureCoreWebView2Async(env);

        var tab = new BrowserTab { Id = _nextId++, View = view };
        var core = view.CoreWebView2;
        // Pop-ups become tabs here rather than separate top-level windows.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            _ = OpenCoreAsync(e.Uri, 0, CancellationToken.None);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (ReferenceEquals(tab, _active)) txtUrl.Text = e.Uri;
        };
        core.NavigationCompleted += (_, e) =>
        {
            tab.Url = core.Source;
            tab.Title = core.DocumentTitle;
            UpdateChip(tab);
            if (ReferenceEquals(tab, _active)) txtUrl.Text = tab.Url;
            tab.NavDone?.TrySetResult(e.IsSuccess);
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = core.DocumentTitle;
            UpdateChip(tab);
        };

        var chip = new Button { Style = (Style)Resources["TabChip"], Content = "new tab", Tag = "off" };
        chip.Click += (_, _) => Activate(tab);
        chip.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) CloseTab(tab);
        };
        var menu = new ContextMenu();
        var close = new MenuItem { Header = "Close tab" };
        close.Click += (_, _) => CloseTab(tab);
        menu.Items.Add(close);
        chip.ContextMenu = menu;
        tab.Chip = chip;

        tabStrip.Children.Add(chip);
        _tabs.Add(tab);
        lblEmpty.Visibility = Visibility.Collapsed;
        AppLogger.Log($"[Browser] tab {tab.Id} opened ({_tabs.Count} open)");
        return tab;
    }

    private void Activate(BrowserTab tab)
    {
        _active = tab;
        foreach (var t in _tabs)
        {
            var on = ReferenceEquals(t, tab);
            t.View.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            t.Chip.Tag = on ? "on" : "off";
        }
        txtUrl.Text = tab.Url;
        btnBack.IsEnabled = tab.View.CoreWebView2?.CanGoBack == true;
    }

    private void CloseTab(BrowserTab tab)
    {
        if (!_tabs.Remove(tab)) return;
        tabStrip.Children.Remove(tab.Chip);
        viewHost.Children.Remove(tab.View);
        try { tab.View.Dispose(); } catch { }
        tab.NavDone?.TrySetResult(false);

        if (ReferenceEquals(_active, tab))
        {
            _active = null;
            if (_tabs.Count > 0) Activate(_tabs[^1]);
            else
            {
                txtUrl.Text = "";
                lblEmpty.Visibility = Visibility.Visible;
            }
        }
        AppLogger.Log($"[Browser] tab {tab.Id} closed ({_tabs.Count} open)");
    }

    private void UpdateChip(BrowserTab tab)
    {
        var label = string.IsNullOrWhiteSpace(tab.Title)
            ? (Uri.TryCreate(tab.Url, UriKind.Absolute, out var u) ? u.Host : "new tab")
            : tab.Title;
        tab.Chip.Content = label.Length > 28 ? label[..28] + "…" : label;
        tab.Chip.ToolTip = tab.Url;
        if (ReferenceEquals(tab, _active)) btnBack.IsEnabled = tab.View.CoreWebView2?.CanGoBack == true;
    }

    private async Task<bool> NavigateAsync(BrowserTab tab, string url, CancellationToken ct)
    {
        tab.NavDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            tab.View.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Browser] navigate failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        using var timeout = new CancellationTokenSource(NavTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await tab.NavDone.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // A page that keeps loading (ads, trackers) still has its document; read what is there.
            return !string.IsNullOrEmpty(tab.View.CoreWebView2?.Source);
        }
    }

    /// <summary>The live DOM, serialised — after scripts ran, which the headless fetch never sees.</summary>
    private static async Task<string> OuterHtmlAsync(BrowserTab tab)
    {
        var json = await tab.View.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
        try { return JsonSerializer.Deserialize<string>(json) ?? ""; }
        catch { return ""; }
    }

    // ── User actions ─────────────────────────────────────────────────────────

    private async void OnNewTabClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Activate(await NewTabAsync());
            txtUrl.Focus();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Browser] new tab failed: {ex.Message}");
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        var core = _active?.View.CoreWebView2;
        if (core?.CanGoBack == true) core.GoBack();
    }

    private void OnReloadClick(object sender, RoutedEventArgs e) => _active?.View.CoreWebView2?.Reload();

    private void OnGoClick(object sender, RoutedEventArgs e) => _ = GoAsync(txtUrl.Text);

    private void OnUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = GoAsync(txtUrl.Text);
    }

    /// <summary>A URL opens as typed; words are searched. Mirrors what a browser's omnibox does.</summary>
    private async Task GoAsync(string input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return;
        try
        {
            var looksLikeUrl = text.Contains("://") || (!text.Contains(' ') && text.Contains('.'));
            var url = looksLikeUrl
                ? (text.Contains("://") ? text : "https://" + text)
                : WebSearchParser.BuildDuckDuckGoUrl(text);
            var tab = _active ?? await NewTabAsync();
            Activate(tab);
            await NavigateAsync(tab, url, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Browser] go failed: {ex.Message}");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
