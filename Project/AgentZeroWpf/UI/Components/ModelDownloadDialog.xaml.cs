using System.Threading;
using System.Windows;
using Agent.Common;
using Agent.Common.Voice;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// Reusable progress dialog for any model-download operation. The dialog
/// itself owns no provider-specific logic — callers pass a
/// <see cref="DownloadFunc"/> delegate that does the actual work and writes
/// status updates through <see cref="IProgress{T}"/>. M0020 follow-up #7 —
/// extracted because Supertonic, Whisper, and LLM GGUF downloads were all
/// growing their own ad-hoc progress UI; this is the first consumer, the
/// others can adopt incrementally.
/// </summary>
public partial class ModelDownloadDialog : Window
{
    public delegate Task<bool> DownloadFunc(IProgress<ModelDownloadStatus> progress, CancellationToken ct);

    private readonly DownloadFunc _download;
    private readonly Action? _clearCache;
    private CancellationTokenSource? _cts;
    private bool _running;
    // Keep the last terminal status so OnStart / FinishUi can log the full
    // diagnostic (stderr-derived Detail) — earlier rounds only logged
    // "ok=False" which made root-cause-from-log-file impossible.
    private ModelDownloadStatus? _lastTerminal;

    /// <summary>
    /// Construct the dialog.
    /// </summary>
    /// <param name="title">Window title + headline (e.g. "Download Supertonic Model").</param>
    /// <param name="description">One-line explainer under the headline (size, cache location, etc.).</param>
    /// <param name="cachePathHint">Optional path shown next to the "Start fresh" checkbox so the user knows what would be wiped.</param>
    /// <param name="download">The actual work — receives a progress sink + cancellation token, returns ok/fail.</param>
    /// <param name="clearCache">Optional cache-wipe action invoked before <paramref name="download"/> when "Start fresh" is ticked. Throws on failure (e.g. locked file).</param>
    public ModelDownloadDialog(
        string title,
        string description,
        string? cachePathHint,
        DownloadFunc download,
        Action? clearCache = null)
    {
        InitializeComponent();
        Title = title;
        tbDialogTitle.Text = title;
        tbDialogDescription.Text = description;
        tbCachePath.Text = string.IsNullOrEmpty(cachePathHint) ? "" : $"({cachePathHint})";
        chkStartFresh.IsEnabled = clearCache is not null;
        _download = download;
        _clearCache = clearCache;
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true;
        btnStart.IsEnabled = false;
        btnClose.IsEnabled = false;
        btnCancel.IsEnabled = true;
        chkStartFresh.IsEnabled = false;
        pb.IsIndeterminate = true;
        pb.Value = 0;
        tbCaption.Text = "Preparing…";
        tbDetail.Text = "";
        _cts = new CancellationTokenSource();

        // Optional cache wipe before launching the downloader — done on a
        // background thread so a slow filesystem doesn't freeze the dialog.
        if (chkStartFresh.IsChecked == true && _clearCache is not null)
        {
            try
            {
                tbCaption.Text = "Clearing cache…";
                await Task.Run(_clearCache);
            }
            catch (Exception ex)
            {
                tbCaption.Text = "Cache clear failed";
                tbDetail.Text = ex.Message + " — close other supertonic processes and retry.";
                pb.IsIndeterminate = false;
                FinishUi(success: false);
                AppLogger.LogError("[ModelDownloadDialog] Cache clear failed", ex);
                return;
            }
        }

        _lastTerminal = null;
        var progress = new Progress<ModelDownloadStatus>(ApplyStatus);
        try
        {
            var ok = await _download(progress, _cts.Token);
            FinishUi(success: ok);
            // Log the full terminal detail (stderr-derived) so the operator
            // can root-cause from app-log.txt without needing the dialog open.
            var detail = _lastTerminal?.Detail ?? "(no terminal status emitted)";
            AppLogger.Log($"[ModelDownloadDialog] '{Title}' finished | ok={ok} | terminal='{detail}'");
        }
        catch (OperationCanceledException)
        {
            tbCaption.Text = "Cancelled.";
            tbDetail.Text = "Partial files may remain in the cache — tick 'Start fresh' if subsequent attempts fail.";
            pb.IsIndeterminate = false;
            FinishUi(success: false);
            AppLogger.Log($"[ModelDownloadDialog] '{Title}' cancelled | terminal='{_lastTerminal?.Detail ?? "(none)"}'");
        }
        catch (Exception ex)
        {
            tbCaption.Text = "Download crashed.";
            tbDetail.Text = ex.Message;
            pb.IsIndeterminate = false;
            FinishUi(success: false);
            AppLogger.LogError($"[ModelDownloadDialog] '{Title}' crashed (last terminal: {_lastTerminal?.Detail ?? "none"})", ex);
        }
    }

    private void ApplyStatus(ModelDownloadStatus s)
    {
        tbCaption.Text = s.Caption;
        tbDetail.Text = s.Detail;
        if (s.PercentComplete is int pct)
        {
            pb.IsIndeterminate = false;
            pb.Value = Math.Clamp(pct, 0, 100);
        }
        else
        {
            pb.IsIndeterminate = !s.IsTerminal;
        }
        if (s.IsTerminal)
        {
            pb.IsIndeterminate = false;
            pb.Foreground = s.IsSuccess
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.OrangeRed;
            _lastTerminal = s;
        }
    }

    private void FinishUi(bool success)
    {
        _running = false;
        btnStart.IsEnabled = !success; // allow retry after a failure
        btnStart.Content = success ? "Start download" : "Retry";
        btnCancel.IsEnabled = false;
        btnClose.IsEnabled = true;
        chkStartFresh.IsEnabled = _clearCache is not null && !success;
        if (success)
        {
            pb.IsIndeterminate = false;
            pb.Value = 100;
            pb.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        _cts?.Dispose();
        _cts = null;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
        btnCancel.IsEnabled = false;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            try { _cts?.Cancel(); } catch { }
        }
        DialogResult = true;
        Close();
    }
}
