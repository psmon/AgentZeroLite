using System;
using System.Linq;
using System.Threading.Tasks;
using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Agent.Common.Music;

namespace AgentZeroWpf.Services.Browser;

/// <summary>One YouTube playlist row as the plugin sees it (field names match agent-band.js).</summary>
public sealed record YouTubePlaylistDto(
    int Id, string VideoId, string Title, string Author, string Thumbnail, string Category, string By, string Url);

/// <summary>
/// SQLite-backed persistence for the Agent Band YouTube stage playlist
/// (B4 / music-curator #29). Replaces the plugin's localStorage store so the
/// playlist shares the app DB's durability, mirroring the MP3 library path.
/// Every op runs off the UI thread against a scoped <see cref="AppDbContext"/>;
/// <c>VideoId</c> is the unique upsert key.
/// </summary>
public partial class WebDevHost
{
    private static YouTubePlaylistDto ToYtDto(YouTubePlaylistItem r)
        => new(r.Id, r.VideoId, r.Title, r.Author, r.Thumbnail, r.Category, r.CategoryBy, r.Url);

    public async Task<object> YtListAsync()
        => await Task.Run(() =>
        {
            using var db = new AppDbContext();
            var rows = db.YouTubePlaylistItems
                .OrderByDescending(t => t.AddedAtUtc).ThenByDescending(t => t.Id)
                .Take(YouTubePlaylistRules.MaxItems)
                .ToList();
            return (object)new { ok = true, items = rows.Select(ToYtDto).ToList() };
        });

    public async Task<object> YtUpsertAsync(
        string? videoId, string? title, string? author, string? thumbnail,
        string? category, string? by, string? url)
        => await Task.Run(() =>
        {
            if (!YouTubePlaylistRules.IsValidVideoId(videoId))
                return (object)new { ok = false, error = "invalid-video-id" };

            using var db = new AppDbContext();
            var row = db.YouTubePlaylistItems.FirstOrDefault(x => x.VideoId == videoId);
            bool isNew = row is null;
            if (row is null)
            {
                row = new YouTubePlaylistItem { VideoId = videoId! };
                db.YouTubePlaylistItems.Add(row);
            }
            row.Title = title ?? "";
            row.Author = author ?? "";
            row.Thumbnail = thumbnail ?? "";
            row.Category = YouTubePlaylistRules.NormalizeCategory(category);
            row.CategoryBy = YouTubePlaylistRules.NormalizeCategoryBy(by);
            row.Url = string.IsNullOrWhiteSpace(url) ? $"https://www.youtube.com/watch?v={videoId}" : url!;
            row.AddedAtUtc = DateTime.UtcNow; // re-paste bumps the entry to newest
            db.SaveChanges();

            // Prune oldest rows beyond the cap (matches the plugin's YT_STORE_MAX).
            var excess = db.YouTubePlaylistItems
                .OrderByDescending(t => t.AddedAtUtc).ThenByDescending(t => t.Id)
                .Skip(YouTubePlaylistRules.MaxItems)
                .ToList();
            if (excess.Count > 0)
            {
                db.YouTubePlaylistItems.RemoveRange(excess);
                db.SaveChanges();
            }
            return (object)new { ok = true, id = row.Id, isNew, item = ToYtDto(row) };
        });

    public async Task<object> YtRemoveAsync(int id, string? videoId)
        => await Task.Run(() =>
        {
            using var db = new AppDbContext();
            var row = id > 0
                ? db.YouTubePlaylistItems.Find(id)
                : db.YouTubePlaylistItems.FirstOrDefault(x => x.VideoId == videoId);
            if (row is null) return (object)new { ok = false, error = "not-found" };
            db.YouTubePlaylistItems.Remove(row);
            db.SaveChanges();
            return (object)new { ok = true, id = row.Id };
        });

    public async Task<object> YtClearAsync()
        => await Task.Run(() =>
        {
            using var db = new AppDbContext();
            var all = db.YouTubePlaylistItems.ToList();
            db.YouTubePlaylistItems.RemoveRange(all);
            db.SaveChanges();
            return (object)new { ok = true, removed = all.Count };
        });
}
