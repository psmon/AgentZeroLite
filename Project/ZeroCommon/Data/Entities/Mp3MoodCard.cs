namespace Agent.Common.Data.Entities;

/// <summary>
/// One LLM-generated "음악 느낌 카드" (M0030 — Agent Band MP3 auto-recommend).
/// A card is a SAVED filter over the MP3 library's tag dimensions (장르 /
/// 가수 / 음악적느낌 / 악기) plus a short human blurb the LLM wrote from the
/// library inventory. Clicking a card in the plugin plays a random matching
/// track and keeps auto-advancing inside the card's pool.
/// <see cref="FiltersJson"/> is a JSON object
/// <c>{ categories: [], artists: [], moods: [], instruments: [] }</c> —
/// stored as JSON (not CSV columns) because artist names may contain commas
/// and the filter shape may grow.
/// </summary>
public class Mp3MoodCard
{
    public int Id { get; set; }

    /// <summary>Card title, e.g. "비 오는 밤의 서정 발라드".</summary>
    public string Title { get; set; } = "";

    /// <summary>1~2 sentence blurb — 포함된 가수·느낌 요약 (LLM 생성).</summary>
    public string Description { get; set; } = "";

    /// <summary>JSON: { categories, artists, moods, instruments } — each a string array.</summary>
    public string FiltersJson { get; set; } = "{}";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
