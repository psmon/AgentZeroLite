using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Agent.Common;
using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Agent.Common.Module;
using AgentZeroWpf.Services;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// Diff Review overlay (W3, orca-adoption). Renders the active workspace's
/// working-tree git diff in a self-contained WebView2 page (no external CDN /
/// Monaco — offline & CSP-safe), lets the reviewer drop inline comments on
/// lines, persists them (<see cref="DiffComment"/>), and ships them back to the
/// agent as a single structured follow-up prompt.
/// </summary>
public partial class DiffReviewPanel : UserControl
{
    private bool _initialized;
    private bool _initializing;

    private Func<string?>? _workspaceRootProvider;
    private Action<string>? _shipPrompt;

    private string _sessionId = Guid.NewGuid().ToString("n");
    private readonly List<DiffComment> _comments = new();

    public DiffReviewPanel()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
    }

    /// <summary>
    /// Wires the panel to its host. <paramref name="workspaceRootProvider"/>
    /// yields the git repo root to diff; <paramref name="shipPrompt"/> delivers
    /// the assembled review prompt to the agent (MainWindow routes it to the
    /// bot actor via StartAgentLoop).
    /// </summary>
    public void Configure(Func<string?> workspaceRootProvider, Action<string> shipPrompt)
    {
        _workspaceRootProvider = workspaceRootProvider;
        _shipPrompt = shipPrompt;
    }

    private async void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible || _initializing) return;
        if (!_initialized)
        {
            _initializing = true;
            try
            {
                await webview.EnsureCoreWebView2Async();
                webview.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                webview.CoreWebView2.NavigateToString(HtmlShell);
                _initialized = true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[DiffReview] WebView2 init failed: {ex.Message}");
                _initializing = false;
                return;
            }
            _initializing = false;
        }
        await RefreshAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        if (webview.CoreWebView2 is null) return;

        var root = _workspaceRootProvider?.Invoke();
        txtWorkspace.Text = string.IsNullOrEmpty(root) ? "(no workspace)" : root;

        // New review snapshot → new session id, drop uncommitted comments.
        _sessionId = Guid.NewGuid().ToString("n");
        _comments.Clear();
        UpdateShipButton();

        var result = await GitDiffService.GetDiffAsync(root);
        if (!result.Ok)
        {
            await CallJsAsync("renderError", JsonSerializer.Serialize(result.Error ?? "diff failed"));
            return;
        }

        var dto = result.Files.Select(f => new
        {
            file = f.NewPath == "/dev/null" ? f.OldPath : f.NewPath,
            isBinary = f.IsBinary,
            isNew = f.IsNew,
            isDeleted = f.IsDeleted,
            hunks = f.Hunks.Select(h => new
            {
                header = h.Header,
                lines = h.Lines.Select(l => new
                {
                    kind = l.Kind switch
                    {
                        GitDiffReader.LineKind.Add => "add",
                        GitDiffReader.LineKind.Delete => "del",
                        _ => "ctx",
                    },
                    oldNo = l.OldLineNo,
                    newNo = l.NewLineNo,
                    text = l.Text,
                }),
            }),
        });

        await CallJsAsync("renderDiff", JsonSerializer.Serialize(dto));
    }

    private void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;
            if (type != "addComment") return;

            var file = root.GetProperty("file").GetString() ?? "";
            var line = root.GetProperty("line").GetInt32();
            var side = root.GetProperty("side").GetString() ?? "new";
            var text = root.GetProperty("text").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(text)) return;

            var comment = new DiffComment
            {
                SessionId = _sessionId,
                FilePath = file,
                LineNumber = line,
                Side = side,
                CommentText = text.Trim(),
                CreatedAtUtc = DateTime.UtcNow,
                Shipped = false,
            };
            _comments.Add(comment);
            PersistComment(comment);
            UpdateShipButton();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[DiffReview] web message parse failed: {ex.Message}");
        }
    }

    private static void PersistComment(DiffComment comment)
    {
        try
        {
            using var db = new AppDbContext();
            db.DiffComments.Add(comment);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[DiffReview] persist failed: {ex.Message}");
        }
    }

    private void UpdateShipButton()
        => btnShip.Content = $"➤  Ship {_comments.Count} to agent";

    private void OnShipClick(object sender, RoutedEventArgs e)
    {
        if (_comments.Count == 0)
        {
            AppLogger.Log("[DiffReview] ship requested with no comments");
            return;
        }
        if (_shipPrompt is null)
        {
            AppLogger.Log("[DiffReview] ship requested but no handler wired");
            return;
        }

        var prompt = BuildReviewPrompt(_comments);
        _shipPrompt(prompt);
        MarkShipped(_comments);
        _comments.Clear();
        UpdateShipButton();
    }

    /// <summary>Formats collected line comments into one structured agent prompt.</summary>
    internal static string BuildReviewPrompt(IReadOnlyList<DiffComment> comments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Please address the following code-review comments on the current diff.");
        sb.AppendLine("Each item is anchored to a file and line:");
        sb.AppendLine();
        foreach (var group in comments.GroupBy(c => c.FilePath))
        {
            sb.AppendLine($"### {group.Key}");
            foreach (var c in group.OrderBy(c => c.LineNumber))
                sb.AppendLine($"- line {c.LineNumber} ({c.Side}): {c.CommentText}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static void MarkShipped(IReadOnlyList<DiffComment> comments)
    {
        try
        {
            using var db = new AppDbContext();
            foreach (var c in comments)
            {
                var row = db.DiffComments.Find(c.Id);
                if (row is not null) row.Shipped = true;
            }
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[DiffReview] mark shipped failed: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task CallJsAsync(string fn, string jsonArg)
    {
        try
        {
            if (webview.CoreWebView2 is null) return;
            await webview.CoreWebView2.ExecuteScriptAsync($"{fn}({jsonArg})");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[DiffReview] JS call {fn} failed: {ex.Message}");
        }
    }

    // Self-contained page: no external resources (offline / CSP-safe). The diff
    // model is pushed in via renderDiff(json); comments post back through
    // chrome.webview.postMessage.
    private const string HtmlShell = """
<!doctype html><html><head><meta charset="utf-8"><style>
  :root { color-scheme: dark; }
  body { background:#1e1e1e; color:#d4d4d4; font-family:Consolas,monospace; font-size:12px; margin:0; padding:0; }
  #empty { padding:24px; color:#888; }
  .file { border-bottom:1px solid #333; }
  .file-head { position:sticky; top:0; background:#252526; padding:6px 12px; font-weight:bold; color:#4ec9b0; border-bottom:1px solid #333; }
  .badge { color:#888; font-weight:normal; margin-left:8px; font-size:11px; }
  .hunk-head { background:#2d2d30; color:#569cd6; padding:2px 12px; }
  table { border-collapse:collapse; width:100%; }
  td { padding:0 6px; white-space:pre-wrap; vertical-align:top; }
  td.gutter { color:#666; text-align:right; user-select:none; width:1%; white-space:nowrap; }
  td.marker { width:1%; color:#666; cursor:pointer; user-select:none; }
  td.marker:hover { color:#4ec9b0; }
  tr.add td.content { background:#0c2a12; }
  tr.del td.content { background:#341414; }
  tr.add td.gutter { background:#0c2a12; }
  tr.del td.gutter { background:#341414; }
  .cbox { margin:4px 0; padding:6px; background:#2d2d30; border:1px solid #4ec9b0; }
  .cbox textarea { width:100%; box-sizing:border-box; background:#1e1e1e; color:#d4d4d4; border:1px solid #444; font-family:Consolas,monospace; font-size:12px; }
  .cbox button { margin-top:4px; background:#0e639c; color:#fff; border:none; padding:3px 10px; cursor:pointer; }
  .saved { color:#4ec9b0; padding:4px 12px; }
</style></head><body>
<div id="root"><div id="empty">Loading diff…</div></div>
<script>
  function post(o){ if(window.chrome&&chrome.webview) chrome.webview.postMessage(o); }
  function esc(s){ return (s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }
  function renderError(msg){ document.getElementById('root').innerHTML='<div id="empty">git diff: '+esc(msg)+'</div>'; }
  function renderDiff(files){
    var root=document.getElementById('root'); root.innerHTML='';
    if(!files||!files.length){ root.innerHTML='<div id="empty">No changes in the working tree.</div>'; return; }
    files.forEach(function(f){
      var fd=document.createElement('div'); fd.className='file';
      var tag = f.isNew?' (new)':f.isDeleted?' (deleted)':f.isBinary?' (binary)':'';
      fd.innerHTML='<div class="file-head">'+esc(f.file)+'<span class="badge">'+tag+'</span></div>';
      if(f.isBinary){ root.appendChild(fd); return; }
      (f.hunks||[]).forEach(function(h){
        var hd=document.createElement('div'); hd.className='hunk-head'; hd.textContent=h.header; fd.appendChild(hd);
        var tbl=document.createElement('table');
        (h.lines||[]).forEach(function(l){
          var tr=document.createElement('tr'); tr.className=(l.kind==='add'?'add':l.kind==='del'?'del':'ctx');
          var sign=l.kind==='add'?'+':l.kind==='del'?'-':' ';
          var side=l.kind==='del'?'old':'new';
          var lineNo=l.kind==='del'?l.oldNo:l.newNo;
          tr.innerHTML='<td class="gutter">'+(l.oldNo||'')+'</td><td class="gutter">'+(l.newNo||'')+'</td>'+
                       '<td class="marker" title="Add comment">💬</td>'+
                       '<td class="content">'+esc(sign+l.text)+'</td>';
          var marker=tr.querySelector('.marker');
          marker.onclick=function(){ openBox(tr,f.file,lineNo,side); };
          tbl.appendChild(tr);
        });
        fd.appendChild(tbl);
      });
      root.appendChild(fd);
    });
  }
  function openBox(afterRow,file,line,side){
    if(afterRow._box){ afterRow._box.querySelector('textarea').focus(); return; }
    var tr=document.createElement('tr'); var td=document.createElement('td'); td.colSpan=4;
    var box=document.createElement('div'); box.className='cbox';
    box.innerHTML='<textarea rows="2" placeholder="Comment on line '+line+'…"></textarea><br><button>Add</button>';
    td.appendChild(box); tr.appendChild(td);
    afterRow.parentNode.insertBefore(tr,afterRow.nextSibling); afterRow._box=box;
    var ta=box.querySelector('textarea'); ta.focus();
    box.querySelector('button').onclick=function(){
      var text=ta.value.trim(); if(!text) return;
      post({type:'addComment',file:file,line:line,side:side,text:text});
      td.innerHTML='<span class="saved">✓ comment added (line '+line+')</span>';
      afterRow._box=null;
    };
  }
</script></body></html>
""";
}
