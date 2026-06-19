using System;

namespace AgentZeroAvalonia.Models;

/// <summary>클립보드 히스토리 1건.</summary>
public sealed class ClipboardEntry
{
    public required string Text { get; init; }
    public DateTime CapturedAt { get; init; } = DateTime.Now;

    public string TimeLabel => CapturedAt.ToString("HH:mm:ss");

    /// <summary>리스트 표시용 한 줄 미리보기(최대 120자).</summary>
    public string Preview
    {
        get
        {
            var firstLine = Text.ReplaceLineEndings(" ⏎ ").Trim();
            return firstLine.Length > 120 ? firstLine[..120] + "…" : firstLine;
        }
    }
}
