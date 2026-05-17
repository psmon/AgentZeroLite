using System.Text.RegularExpressions;

namespace Agent.Common.Voice;

/// <summary>
/// Parses a single line of <c>tqdm</c> progress output emitted by the
/// HuggingFace Hub library inside <see cref="SuperTonicTts.PrewarmScript"/>'s
/// subprocess. tqdm in non-TTY mode (which subprocess gives it) prints one
/// full line per update — e.g.:
/// <code>
/// Fetching 26 files:  23%|##3       | 6/26 [00:04&lt;00:15,  1.32it/s]
/// </code>
/// so we can grab the percent + counts + ETA + rate per line without
/// having to deal with carriage-return rewrites.
///
/// Returns null for lines that aren't progress (banners, warnings, blank
/// lines) so the caller can ignore them without branching on regex success.
/// </summary>
internal static class SuperTonicProgressParser
{
    // Capture groups:
    //   caption   — "Fetching 26 files"
    //   percent   — "23"
    //   current   — "6"
    //   total     — "26"
    //   elapsed   — "00:04"      (optional)
    //   remaining — "00:15"      (optional)
    //   rate      — "1.32it/s"   (optional)
    private static readonly Regex TqdmLine = new(
        @"^(?<caption>[^:]+?):\s+(?<percent>\d+)%\|[^|]*\|\s*(?<current>\d+)/(?<total>\d+)" +
        @"(?:\s+\[(?<elapsed>[^<]+)<(?<remaining>[^,\]]+)(?:,\s*(?<rate>[^\]]+))?\])?\s*$",
        RegexOptions.Compiled);

    public static ModelDownloadStatus? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var m = TqdmLine.Match(line.TrimEnd('\r'));
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups["percent"].Value, out var pct)) return null;

        var caption = m.Groups["caption"].Value.Trim();
        var current = m.Groups["current"].Value;
        var total = m.Groups["total"].Value;
        var detailParts = new List<string> { $"{current} / {total}" };
        if (m.Groups["elapsed"].Success && m.Groups["remaining"].Success)
            detailParts.Add($"{m.Groups["elapsed"].Value} elapsed, {m.Groups["remaining"].Value} left");
        if (m.Groups["rate"].Success)
            detailParts.Add(m.Groups["rate"].Value.Trim());

        return new ModelDownloadStatus(
            Caption: caption,
            Detail: string.Join(" · ", detailParts),
            PercentComplete: Math.Clamp(pct, 0, 100),
            IsTerminal: false,
            IsSuccess: false);
    }
}
