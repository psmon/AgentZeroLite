namespace Agent.Common.Data.Entities;

/// <summary>
/// One local MP3 file in the Agent Band playlist (M0029). Rows live in the
/// app DB (<c>%LOCALAPPDATA%\AgentZeroLite\agentZeroLite.db</c>) so the
/// playlist survives app reinstall/migration — unlike the YouTube playlist,
/// which is plugin-side localStorage. <see cref="FilePath"/> is the upsert
/// key (unique index); a rescan updates tag fields in place and never
/// duplicates. <see cref="Instruments"/> is a CSV of canonical performer
/// keys ("piano,violin,vocal") accumulated LIVE while the track plays —
/// the AST loopback classifier hears the actual audio, so the set gets
/// richer every time the song is played.
/// </summary>
public class Mp3Track
{
    public int Id { get; set; }

    /// <summary>Absolute path — unique upsert key.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>Path relative to the scan root, '/'-separated (virtual-host URL part).</summary>
    public string RelativePath { get; set; } = "";

    public string FileName { get; set; } = "";

    /// <summary>Immediate parent folder name — a classification hint (e.g. "OST", "발라드").</summary>
    public string FolderName { get; set; } = "";

    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";

    /// <summary>Genre string straight from the ID3 tag (may be empty/nonsense).</summary>
    public string TagGenre { get; set; } = "";

    /// <summary>Category label from the shared Agent Band category set (재즈/K-Pop/…).</summary>
    public string Category { get; set; } = "";

    /// <summary>Provenance: "llm" | "tag" | "none" (pending) — mirrors the YT list's badge.</summary>
    public string CategoryBy { get; set; } = "";

    /// <summary>
    /// LLM-judged vocal gender for the MP3 stage director (후속 #3):
    /// "male" | "female" | "group" | "" (unknown). Tag/LLM verdict wins over
    /// live audio detection — vocal range makes audio-only gender unreliable.
    /// </summary>
    public string VocalGender { get; set; } = "";

    /// <summary>CSV of canonical instrument/vocal keys heard during playback ("piano,drum,vocal").</summary>
    public string Instruments { get; set; } = "";

    /// <summary>
    /// CSV of canonical mood keys heard during playback (M0030 — AST의
    /// "Happy/Sad/Exciting music" 계열 무드 라벨): "happy,exciting,tender".
    /// 악기와 같은 실시간 누적·클램프 경로를 탄다.
    /// </summary>
    public string Moods { get; set; } = "";

    public double DurationSeconds { get; set; }
    public long FileSizeBytes { get; set; }

    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastPlayedAtUtc { get; set; }
    public int PlayCount { get; set; }
}
