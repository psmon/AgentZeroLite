using System;

namespace Agent.Common.Data.Entities;

/// <summary>
/// One saved Agent Band YouTube stage entry. Migrated from the plugin's
/// localStorage (<c>agentBand.playlist.v1</c>) to SQLite so the playlist shares
/// the same durable persistence layer as the MP3 library (music-curator #29 /
/// M0026). <see cref="VideoId"/> is the unique upsert key — re-pasting a video
/// updates the existing row rather than duplicating it.
/// </summary>
public class YouTubePlaylistItem
{
    public int Id { get; set; }

    /// <summary>11-char YouTube id — unique index, the dedupe/upsert key.</summary>
    public string VideoId { get; set; } = "";

    public string Title { get; set; } = "";
    public string Author { get; set; } = "";      // channel
    public string Thumbnail { get; set; } = "";
    public string Category { get; set; } = "";     // one of the plugin's YT_CATEGORIES
    public string CategoryBy { get; set; } = "";   // provenance: "llm" | "keyword"
    public string Url { get; set; } = "";

    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
}
