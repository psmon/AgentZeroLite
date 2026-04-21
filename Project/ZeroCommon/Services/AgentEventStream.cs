using System.Text.RegularExpressions;
using Agent.Common;

namespace Agent.Common.Services;

/// <summary>
/// Semantic events emitted by the agent event stream.
/// These represent meaning — approval, tool progress, etc. —
/// rather than raw terminal output bytes.
/// </summary>
public abstract record AgentEvent(DateTimeOffset Timestamp);

public sealed record ApprovalRequested(
    string Command,
    IReadOnlyList<ApprovalParser.ApprovalOption> Options,
    bool IsFallback,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record ApprovalDismissed(DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record UrlDetected(
    string Url,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

/// <summary>
/// Processes terminal output frames and emits semantic AgentEvents.
/// Replaces the DispatcherTimer-based approval watcher with an event-driven approach.
/// </summary>
public sealed class AgentEventStream : IDisposable
{
    private readonly ITerminalSession _session;
    private string _recentBuffer = "";
    private string? _lastApprovalFingerprint;
    private int _urlScanOffset;
    private const int RecentBufferMaxLen = 4000;
    private bool _disposed;

    private static readonly Regex UrlRegex = new(
        @"https?://[^\s""'<>\]\)]+|localhost:\d{1,5}[^\s""'<>\]\)]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public event Action<AgentEvent>? EventReceived;

    public AgentEventStream(ITerminalSession session)
    {
        _session = session;
        _session.OutputReceived += OnOutputReceived;
    }

    /// <summary>
    /// Reset state (e.g. when switching terminal sessions).
    /// </summary>
    public void Reset()
    {
        _recentBuffer = "";
        _lastApprovalFingerprint = null;
        _urlScanOffset = 0;
    }

    private void OnOutputReceived(TerminalOutputFrame frame)
    {
        if (_disposed) return;

        var cleanChunk = ApprovalParser.StripAnsiCodes(frame.Text);
        var prevLen = _recentBuffer.Length;
        _recentBuffer = ApprovalParser.AppendToBuffer(_recentBuffer, cleanChunk, RecentBufferMaxLen);

        // Adjust scan offset if buffer was trimmed
        if (_recentBuffer.Length < prevLen + cleanChunk.Length)
            _urlScanOffset = Math.Max(0, _urlScanOffset - (prevLen + cleanChunk.Length - _recentBuffer.Length));

        // Check for URLs in the buffer (from last scanned position)
        ScanForUrls();

        // Check for approval pattern
        if (!ApprovalParser.ContainsApprovalPattern(_recentBuffer))
            return;

        // Dedup
        var fingerprint = ApprovalParser.GetApprovalFingerprint(_recentBuffer);
        if (fingerprint != null && fingerprint == _lastApprovalFingerprint)
            return;

        var approval = ApprovalParser.ParseApprovalPrompt(_recentBuffer);
        if (approval is null || approval.Options.Count == 0)
            return;

        // Fallback = likely false positive
        if (approval.IsFallback)
        {
            AppLogger.Log("[EventStream] Approval fallback — skipping, likely false positive");
            _recentBuffer = "";
            return;
        }

        _lastApprovalFingerprint = fingerprint;
        _recentBuffer = "";

        AppLogger.Log($"[EventStream] Approval detected: cmd=[{approval.Command}], {approval.Options.Count} options");

        EventReceived?.Invoke(new ApprovalRequested(
            approval.Command,
            approval.Options,
            approval.IsFallback,
            DateTimeOffset.UtcNow));
    }

    private void ScanForUrls()
    {
        // Scan from last offset, but leave a margin at the tail
        // because the URL might be incomplete (split across chunks)
        var safeEnd = _recentBuffer.LastIndexOf('\n');
        if (safeEnd < 0) safeEnd = _recentBuffer.LastIndexOf(' ');
        if (safeEnd <= _urlScanOffset) return;

        var region = _recentBuffer[_urlScanOffset..safeEnd];

        foreach (Match m in UrlRegex.Matches(region))
        {
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', '\u2026');

            // Skip truncated URLs (ending with ellipsis)
            if (url.EndsWith("\u2026") || url.EndsWith("...")) continue;

            // localhost without scheme → prepend http://
            if (url.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
                url = "http://" + url;

            EventReceived?.Invoke(new UrlDetected(url, DateTimeOffset.UtcNow));
        }

        _urlScanOffset = safeEnd;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.OutputReceived -= OnOutputReceived;
    }
}
