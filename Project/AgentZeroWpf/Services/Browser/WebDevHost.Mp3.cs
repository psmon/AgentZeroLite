using System.IO;
using Agent.Common;
using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Agent.Common.Llm;
using Agent.Common.Mp3;
using Agent.Common.Vision;

namespace AgentZeroWpf.Services.Browser;

/// <summary>Live scan-job progress (M0029). <c>Phase</c> = "enumerate" | "scan" | "done".</summary>
public sealed record Mp3ScanProgressInfo(string Phase, int Done, int Total, string Current, int Added, int Updated, int Classified);

/// <summary>Scan-job completion summary. <c>Error</c> is null on success, "cancelled" on user cancel.</summary>
public sealed record Mp3ScanDoneInfo(bool Ok, int Total, int Added, int Updated, int Classified, string? Error);

/// <summary>One playlist row as the plugin sees it. <c>Available</c> = file exists under the CURRENT scan root (playable via mp3.local).</summary>
public sealed record Mp3TrackDto(
    int Id, string RelativePath, string FileName, string FolderName, string Title, string Artist,
    string Album, string TagGenre, string Category, string CategoryBy, string VocalGender,
    string Instruments, double DurationSeconds, int PlayCount, DateTime? LastPlayedAtUtc, bool Available);

/// <summary>
/// Local-MP3 playlist surface for the Agent Band plugin (M0029). The playlist
/// lives in the SQLite app DB (<see cref="Mp3Track"/>) so it survives
/// reinstall — separate from the YouTube playlist (plugin localStorage).
///
/// The scan is a BACKGROUND JOB (M0029 확장2): <see cref="Mp3StartScan"/>
/// returns immediately and the job streams <see cref="Mp3TrackUpserted"/> +
/// <see cref="Mp3ScanProgress"/> events, so already-scanned tracks are
/// playable while the rest of the folder is still being read. Each NEW track
/// fires twice — once right after the tag upsert (playable, category
/// pending) and again after the one-shot LLM classification (tag + filename
/// + folder hint → one category, same throwaway-session pattern as
/// <see cref="ClassifyAsync"/>).
///
/// Playback itself never crosses the bridge: the WebDev panel's bridge maps
/// the scan root to <c>https://mp3.local/</c> (virtual host), and the
/// plugin's &lt;audio&gt; element streams straight from disk. The band hears
/// it through the existing SystemLoopback capture — no new audio wiring.
/// </summary>
public sealed partial class WebDevHost
{
    private CancellationTokenSource? _mp3ScanCts;
    private int _mp3ScanRunning;                    // 0/1 — single-flight scan job
    private Mp3ScanProgressInfo? _mp3LastProgress;  // last emitted, for mp3.status polling

    public event Action<Mp3ScanProgressInfo>? Mp3ScanProgress;
    public event Action<Mp3TrackDto>? Mp3TrackUpserted;
    public event Action<Mp3ScanDoneInfo>? Mp3ScanDone;

    /// <summary>{ folder, folderExists, scanning, progress?, count }.</summary>
    public object GetMp3Status()
    {
        var folder = Mp3SettingsStore.Load().ScanFolder;
        int count = 0;
        try { using var db = new AppDbContext(); count = db.Mp3Tracks.Count(); } catch { }
        return new
        {
            folder,
            folderExists = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder),
            scanning = _mp3ScanRunning == 1,
            progress = _mp3ScanRunning == 1 ? _mp3LastProgress : null,
            count,
        };
    }

    /// <summary>Persist the scan root. The bridge (owner of the WebView2) maps it to mp3.local.</summary>
    public object Mp3SetFolder(string folder)
    {
        folder = (folder ?? "").Trim();
        if (folder.Length == 0 || !Directory.Exists(folder))
            return new { ok = false, error = "folder-missing", folder };
        var s = Mp3SettingsStore.Load();
        s.ScanFolder = folder;
        Mp3SettingsStore.Save(s);
        AppLogger.Log($"[WebDev:Mp3] scan folder set → {folder}");
        return new { ok = true, folder };
    }

    /// <summary>
    /// Kick the background scan job (fire-and-forget; single-flight). Returns
    /// { ok, started } immediately — progress/track/done arrive as events.
    /// </summary>
    public object Mp3StartScan(IReadOnlyList<string> categories)
    {
        var root = Mp3SettingsStore.Load().ScanFolder;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new { ok = false, error = "folder-missing" };
        if (Interlocked.CompareExchange(ref _mp3ScanRunning, 1, 0) != 0)
            return new { ok = false, error = "busy" };

        var cts = new CancellationTokenSource();
        _mp3ScanCts = cts;
        _ = Task.Run(() => Mp3ScanJobAsync(root, categories, cts.Token), CancellationToken.None);
        AppLogger.Log($"[WebDev:Mp3] scan job started | root={root}");
        return new { ok = true, started = true };
    }

    public object Mp3CancelScan()
    {
        try { _mp3ScanCts?.Cancel(); } catch { }
        return new { ok = true, cancelling = _mp3ScanRunning == 1 };
    }

    /// <summary>All playlist rows, newest first. Runs off the UI thread (DispatchAsync is UI-bound).</summary>
    public async Task<object> Mp3ListAsync()
    {
        var root = Mp3SettingsStore.Load().ScanFolder;
        return await Task.Run(() =>
        {
            using var db = new AppDbContext();
            var rows = db.Mp3Tracks.OrderByDescending(t => t.AddedAtUtc).ThenBy(t => t.Id).ToList();
            return (object)new
            {
                ok = true,
                folder = root,
                tracks = rows.Select(r => ToDto(r, root)).ToList(),
            };
        });
    }

    public async Task<object> Mp3RemoveAsync(int id)
        => await Task.Run(() =>
        {
            using var db = new AppDbContext();
            var row = db.Mp3Tracks.Find(id);
            if (row is null) return (object)new { ok = false, error = "not-found" };
            db.Mp3Tracks.Remove(row);
            db.SaveChanges();
            return (object)new { ok = true, id };
        });

    public async Task<object> Mp3MarkPlayedAsync(int id)
        => await Task.Run(() =>
        {
            using var db = new AppDbContext();
            var row = db.Mp3Tracks.Find(id);
            if (row is null) return (object)new { ok = false, error = "not-found" };
            row.PlayCount++;
            row.LastPlayedAtUtc = DateTime.UtcNow;
            db.SaveChanges();
            return (object)new { ok = true, id, playCount = row.PlayCount };
        });

    /// <summary>
    /// M0029 확장 — merge instrument keys heard by the live AST tick into the
    /// track's persisted set. The union is clamped by
    /// <see cref="Mp3InstrumentSet"/> so JS can never store junk.
    /// </summary>
    public async Task<object> Mp3SetInstrumentsAsync(int id, IReadOnlyList<string> instruments)
        => await Task.Run(() =>
        {
            using var db = new AppDbContext();
            var row = db.Mp3Tracks.Find(id);
            if (row is null) return (object)new { ok = false, error = "not-found" };
            var merged = Mp3InstrumentSet.Merge(row.Instruments, instruments);
            if (merged != row.Instruments)
            {
                row.Instruments = merged;
                db.SaveChanges();
            }
            return (object)new { ok = true, id, instruments = Mp3InstrumentSet.Parse(merged) };
        });

    /// <summary>
    /// APIC cover art for a track, or null (no tag picture / file gone).
    /// Served by the bridge's <c>https://mp3.local/__cover/{id}</c> route so
    /// the plugin's now-playing card can show it with a plain &lt;img&gt;.
    /// </summary>
    public (byte[] Data, string Mime)? TryGetMp3Cover(int id)
    {
        try
        {
            string? path;
            using (var db = new AppDbContext()) path = db.Mp3Tracks.Find(id)?.FilePath;
            if (path is null || !File.Exists(path)) return null;
            return Mp3Scanner.ReadCover(path);
        }
        catch { return null; }
    }

    /// <summary>
    /// 후속 #5 — cover-art vision gender. Runs Florence-2 OD on the track's
    /// APIC cover (probe-validated: covers WITH a person yield man/woman
    /// labels; illustration covers yield none). Called lazily by the plugin
    /// when a track starts playing with no gender verdict yet. Priority
    /// contract: the verdict persists into VocalGender only while it's
    /// EMPTY — a later LLM verdict (artist knowledge) overwrites it, and the
    /// plugin's env-sound guess never persists at all.
    /// Reuses the Vision partial's interpreter + single-flight gate.
    /// </summary>
    public async Task<object> Mp3CoverGenderAsync(int id)
    {
        string? path;
        string existing;
        using (var db = new AppDbContext())
        {
            var row = db.Mp3Tracks.Find(id);
            if (row is null) return new { ok = false, error = "not-found" };
            path = row.FilePath;
            existing = row.VocalGender;
        }
        if (existing.Length > 0) return new { ok = true, id, gender = existing, by = "cached" };
        if (!File.Exists(path)) return new { ok = false, error = "file-missing" };

        var s = VisionSettingsStore.Load();
        var dir = VisionSettingsStore.ResolveModelDir(s);
        if (!VisionSettingsStore.IsModelPresent(dir)) return new { ok = false, error = "model-missing" };

        var cover = Mp3Scanner.ReadCover(path);
        if (cover is null) return new { ok = true, id, gender = "", by = "no-cover" };

        if (Interlocked.CompareExchange(ref _visionInFlight, 1, 0) != 0)
            return new { ok = false, error = "busy" };
        try
        {
            _visionInterpreter ??= new Florence2VisionInterpreter(s);
            if (!await _visionInterpreter.EnsureReadyAsync(null))
                return new { ok = false, error = "model-missing" };

            var r = await _visionInterpreter.InterpretAsync(cover.Value.Data);
            var (gender, persons) = CoverGenderFromDetections(r.Detections);
            if (gender.Length > 0)
            {
                using var db = new AppDbContext();
                var row = db.Mp3Tracks.Find(id);
                if (row is not null && row.VocalGender.Length == 0)
                {
                    row.VocalGender = gender;
                    db.SaveChanges();
                }
            }
            AppLogger.Log($"[WebDev:Mp3] cover gender | id={id} → {(gender.Length > 0 ? gender : "?")} " +
                          $"(persons={persons}, {(int)r.InferenceTime.TotalMilliseconds}ms)");
            return new { ok = true, id, gender, by = "vision", persons,
                         inferMs = (int)r.InferenceTime.TotalMilliseconds };
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Mp3] cover gender failed: {ex.GetType().Name}: {ex.Message}");
            return new { ok = false, error = ex.Message };
        }
        finally { Interlocked.Exchange(ref _visionInFlight, 0); }
    }

    // 프로브 검증 결과에 맞춘 판정: person 3+ → 그룹(걸그룹 무대 컨셉),
    // 여성 단독 라벨 → female, 남성 단독 → male, 혼재/무인물 → 보류("").
    private static (string Gender, int Persons) CoverGenderFromDetections(
        IReadOnlyList<Agent.Common.Vision.VisionDetection> detections)
    {
        bool female = false, male = false;
        int persons = 0;
        foreach (var d in detections)
        {
            var l = d.Label.ToLowerInvariant();
            if (PersonRe.IsMatch(l)) persons++;
            if (System.Text.RegularExpressions.Regex.IsMatch(l, @"\b(woman|women|girl|lady|female)\b")) female = true;
            if (System.Text.RegularExpressions.Regex.IsMatch(l, @"\b(man|men|boy)\b|\bmale\b")) male = true;
        }
        if (persons >= 3) return ("group", persons);
        if (female && !male) return ("female", persons);
        if (male && !female) return ("male", persons);
        return ("", persons);
    }

    internal void DisposeMp3()
    {
        try { _mp3ScanCts?.Cancel(); } catch { }
        _mp3ScanCts = null;
    }

    // ─── scan job ────────────────────────────────────────────────────────

    private async Task Mp3ScanJobAsync(string root, IReadOnlyList<string> categories, CancellationToken ct)
    {
        int added = 0, updated = 0, classified = 0, done = 0, total = 0;
        string? error = null;
        long lastEmitMs = 0;

        void EmitProgress(string phase, string current, bool force = false)
        {
            var now = Environment.TickCount64;
            if (!force && now - lastEmitMs < 250) return;
            lastEmitMs = now;
            _mp3LastProgress = new Mp3ScanProgressInfo(phase, done, total, current, added, updated, classified);
            Mp3ScanProgress?.Invoke(_mp3LastProgress);
        }

        try
        {
            EmitProgress("enumerate", root, force: true);
            var files = Mp3Scanner.EnumerateMp3Files(root);
            total = files.Count;
            EmitProgress("scan", "", force: true);

            // ── Phase 1 (후속 #2) — fast tag pass, NO LLM ─────────────────
            // Every file lands in the list as 미분류 first, so the operator
            // sees (and can play) the FULL list at tag-read speed instead of
            // waiting on a per-file LLM call. Classification is a separate
            // backlog pass below.
            var backlog = new List<(int Id, Mp3ScannedFile File, bool HasCategory)>();
            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                var f = Mp3Scanner.ReadFile(root, path);

                Mp3Track row;
                bool needsClassify;
                using (var db = new AppDbContext())
                {
                    var existing = db.Mp3Tracks.FirstOrDefault(t => t.FilePath == f.FilePath);
                    if (existing is null)
                    {
                        row = new Mp3Track { FilePath = f.FilePath, CategoryBy = "none" };
                        db.Mp3Tracks.Add(row);
                        added++;
                    }
                    else
                    {
                        row = existing;
                        updated++;
                    }
                    row.RelativePath = f.RelativePath;
                    row.FileName = f.FileName;
                    row.FolderName = f.FolderName;
                    row.Title = f.Title;
                    row.Artist = f.Artist;
                    row.Album = f.Album;
                    row.TagGenre = f.TagGenre;
                    row.DurationSeconds = f.DurationSeconds;
                    row.FileSizeBytes = f.FileSizeBytes;
                    db.SaveChanges();
                    needsClassify = row.CategoryBy != "llm";   // retry non-LLM verdicts on rescan
                }

                done++;
                // Playable NOW — the plugin appends/updates its list from this
                // event while the scan keeps running (M0029 확장2).
                Mp3TrackUpserted?.Invoke(ToDto(row, root));
                EmitProgress("scan", f.Title);

                if (needsClassify && categories.Count > 0)
                    backlog.Add((row.Id, f, row.Category.Length > 0));
            }

            // ── Phase 2 — async classify pass over the 미분류 backlog ─────
            // One LLM call per pending track; each verdict lands as its own
            // mp3.track event so categories fill in live while the operator
            // is already browsing/playing the list. 후속 #3 효율: rows that
            // already hold an LLM verdict never re-enter the backlog (phase 1
            // filter), and when the LLM is OFF, rows that already have a
            // tag/fallback category are skipped too — nothing would change.
            bool llmOn = LlmGateway.IsActiveAvailable();
            done = 0;
            total = backlog.Count;
            if (total > 0) EmitProgress("classify", "", force: true);
            foreach (var (id, f, hasCategory) in backlog)
            {
                ct.ThrowIfCancellationRequested();
                if (!llmOn && hasCategory) { done++; continue; }
                var (cat, by, gender) = await Mp3ClassifyAsync(f, categories, ct);
                done++;
                if (cat is not null)
                {
                    using var db = new AppDbContext();
                    var again = db.Mp3Tracks.Find(id);
                    if (again is not null
                        && (cat != again.Category || by != again.CategoryBy
                            || (gender.Length > 0 && gender != again.VocalGender)))
                    {
                        again.Category = cat;
                        again.CategoryBy = by;
                        if (gender.Length > 0) again.VocalGender = gender;
                        db.SaveChanges();
                        if (by == "llm") classified++;
                        Mp3TrackUpserted?.Invoke(ToDto(again, root));
                    }
                }
                EmitProgress("classify", f.Title);
            }
            EmitProgress("done", "", force: true);
        }
        catch (OperationCanceledException) { error = "cancelled"; }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLogger.Log($"[WebDev:Mp3] scan job failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _mp3ScanRunning, 0);
            _mp3ScanCts = null;
            _mp3LastProgress = null;
            AppLogger.Log($"[WebDev:Mp3] scan job finished | total={total} added={added} updated={updated} llm={classified} error={error ?? "none"}");
            Mp3ScanDone?.Invoke(new Mp3ScanDoneInfo(error is null, total, added, updated, classified, error));
        }
    }

    /// <summary>
    /// One-shot category + vocal gender for a scanned file. LLM first
    /// (throwaway session, whitelist-clamped — same defense as
    /// <see cref="ClassifyAsync"/>); falls back to the ID3 genre tag when the
    /// LLM is off, then to the last category ("기타"). Gender (후속 #3 — MP3
    /// 무대 연출용) comes only from the LLM's artist knowledge: "male" |
    /// "female" | "group" | "" — the stage director defaults unknowns to a
    /// male soloist and lets live audio flip it, but a tag verdict wins.
    /// </summary>
    private async Task<(string? Category, string By, string Gender)> Mp3ClassifyAsync(
        Mp3ScannedFile f, IReadOnlyList<string> categories, CancellationToken ct)
    {
        var allowed = categories.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
        if (allowed.Count == 0) return (null, "none", "");
        var fallback = allowed[^1];

        if (LlmGateway.IsActiveAvailable())
        {
            try
            {
                await _chatLock.WaitAsync(ct);
                try
                {
                    await using var session = LlmGateway.OpenSession();
                    var list = string.Join(", ", allowed);
                    var hint = Mp3Scanner.BuildClassifyHint(f);
                    var prompt =
                        "너는 음악 분류기야. 아래 로컬 MP3 곡의 (1) 장르 카테고리와 (2) 보컬 성별을 판정해.\n" +
                        "아티스트나 곡 제목을 알면 그 지식으로 추정해도 좋아. 태그장르/폴더명도 참고해.\n" +
                        $"카테고리(하나만): {list}\n" +
                        "성별(하나만): 남성, 여성, 그룹, 모름 — 가수를 모르면 반드시 모름.\n" +
                        "답변은 '카테고리|성별' 형식 한 줄만. 다른 말은 절대 쓰지 마.\n" +
                        "예) IU - 좋은 날 → K-Pop|여성 / 쇼팽 녹턴 → 클래식|모름 / BTS - Butter → K-Pop|그룹\n\n" +
                        hint;
                    var reply = ((await session.SendAsync(prompt, ct)) ?? "").Trim();
                    var match = MatchCategory(reply, allowed);
                    if (match is not null)
                    {
                        var gender = ParseVocalGender(reply);
                        AppLogger.Log($"[WebDev:Mp3] classify | '{Trunc(f.Title, 40)}' → {match} / {(gender.Length > 0 ? gender : "?")}");
                        return (match, "llm", gender);
                    }
                }
                finally { _chatLock.Release(); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Log($"[WebDev:Mp3] classify failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // LLM off / no match — the ID3 genre tag often literally contains the
        // category word ("Jazz", "발라드"), so clamp it against the whitelist.
        var byTag = MatchCategory(f.TagGenre, allowed);
        return byTag is not null ? (byTag, "tag", "") : (fallback, "fallback", "");
    }

    // Clamp the LLM's free-text gender verdict. 여성 before 남성 so "여성" never
    // half-matches; English guarded so "female" can't read as "male".
    private static string ParseVocalGender(string reply)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(reply, "여성|female", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "female";
        if (System.Text.RegularExpressions.Regex.IsMatch(reply, "남성|(?<!fe)male", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "male";
        if (System.Text.RegularExpressions.Regex.IsMatch(reply, "그룹|group|듀엣|duet", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "group";
        return "";
    }

    private static Mp3TrackDto ToDto(Mp3Track r, string currentRoot)
    {
        // Relative to the CURRENT root — a track scanned under an old root
        // stays listed but unplayable (Available=false) until re-scanned.
        var rel = Mp3Scanner.ToRelativePath(currentRoot, r.FilePath);
        bool available = rel.Length > 0 && File.Exists(r.FilePath);
        return new Mp3TrackDto(
            r.Id, rel, r.FileName, r.FolderName, r.Title, r.Artist, r.Album, r.TagGenre,
            r.Category, r.CategoryBy, r.VocalGender, r.Instruments, r.DurationSeconds,
            r.PlayCount, r.LastPlayedAtUtc, available);
    }
}
