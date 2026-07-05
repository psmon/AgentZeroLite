using System.IO;
using System.Text.Json;
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
    string Instruments, string Moods, double DurationSeconds, int PlayCount,
    DateTime? LastPlayedAtUtc, bool Available);

/// <summary>One saved 느낌 카드 (M0030). <c>FiltersJson</c> = { categories, artists, moods, instruments }.</summary>
public sealed record Mp3CardDto(int Id, string Title, string Description, string FiltersJson, DateTime CreatedAtUtc);

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
    /// <summary>
    /// M0030 후속#1 — upserted tracks are BATCHED (~2s / 100건) instead of
    /// one event per file: per-file events flooded the WPF dispatcher +
    /// WebView2 postMessage during fast tag scans and made the whole UI lag.
    /// </summary>
    public event Action<IReadOnlyList<Mp3TrackDto>>? Mp3TrackBatch;
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

    /// <summary>
    /// Playlist rows, newest first, PAGED (M0030 후속#4) — a 5k+ library made
    /// the all-at-once list slow (row-per-File.Exists + full serialize over
    /// postMessage). limit ≤ 0 = everything (back-compat). Returns
    /// { ok, folder, total, offset, tracks } so the plugin can paint the
    /// first page instantly and hydrate the rest in the background.
    /// </summary>
    public async Task<object> Mp3ListAsync(int offset = 0, int limit = 0)
    {
        var root = Mp3SettingsStore.Load().ScanFolder;
        return await Task.Run(() =>
        {
            using var db = new AppDbContext();
            int total = db.Mp3Tracks.Count();
            var q = db.Mp3Tracks.OrderByDescending(t => t.AddedAtUtc).ThenBy(t => t.Id).AsQueryable();
            if (offset > 0) q = q.Skip(offset);
            if (limit > 0) q = q.Take(limit);
            var rows = q.ToList();
            return (object)new
            {
                ok = true,
                folder = root,
                total,
                offset,
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
    /// M0030 — merge mood keys (AST "Happy/Sad/Exciting music" 계열) heard by
    /// the live tick into the track's persisted mood set. Same clamp path as
    /// instruments — JS can never store junk.
    /// </summary>
    public async Task<object> Mp3SetMoodsAsync(int id, IReadOnlyList<string> moods)
        => await Task.Run(() =>
        {
            using var db = new AppDbContext();
            var row = db.Mp3Tracks.Find(id);
            if (row is null) return (object)new { ok = false, error = "not-found" };
            var merged = Mp3InstrumentSet.Merge(row.Moods, moods);
            if (merged != row.Moods)
            {
                row.Moods = merged;
                db.SaveChanges();
            }
            return (object)new { ok = true, id, moods = Mp3InstrumentSet.Parse(merged) };
        });

    // ─── 느낌 카드 (M0030 — 자동추천) ────────────────────────────────────

    public async Task<object> Mp3CardsAsync()
        => await Task.Run(() =>
        {
            using var db = new AppDbContext();
            var cards = db.Mp3MoodCards.OrderByDescending(c => c.CreatedAtUtc).ThenBy(c => c.Id)
                .Select(c => new Mp3CardDto(c.Id, c.Title, c.Description, c.FiltersJson, c.CreatedAtUtc))
                .ToList();
            return (object)new { ok = true, cards };
        });

    public async Task<object> Mp3CardRemoveAsync(int id)
        => await Task.Run(() =>
        {
            using var db = new AppDbContext();
            var row = db.Mp3MoodCards.Find(id);
            if (row is null) return (object)new { ok = false, error = "not-found" };
            db.Mp3MoodCards.Remove(row);
            db.SaveChanges();
            return (object)new { ok = true, id };
        });

    /// <summary>
    /// "카드생성하기" — the LLM curates ONE mood card from the library's tag
    /// inventory (장르/가수/느낌/악기, all mined from the DB) and a
    /// don't-repeat list of existing card titles. Every filter value in the
    /// reply is clamped against the inventory, so the card can only ever
    /// select tracks that actually exist. Persisted; returns the card DTO.
    /// </summary>
    public async Task<object> Mp3CardCreateAsync(CancellationToken ct = default)
    {
        if (!LlmGateway.IsActiveAvailable())
            return new { ok = false, error = "llm-not-ready" };

        List<string> cats, artists, moods, insts, existingTitles;
        using (var db = new AppDbContext())
        {
            var rows = db.Mp3Tracks
                .Select(t => new { t.Category, t.Artist, t.Moods, t.Instruments })
                .ToList();
            if (rows.Count == 0) return new { ok = false, error = "empty-library" };

            cats = rows.Where(r => r.Category.Length > 0)
                .GroupBy(r => r.Category).OrderByDescending(g => g.Count())
                .Select(g => g.Key).ToList();
            artists = rows.Where(r => !string.IsNullOrWhiteSpace(r.Artist))
                .GroupBy(r => r.Artist.Trim()).OrderByDescending(g => g.Count())
                .Select(g => g.Key).Take(30).ToList();
            moods = rows.SelectMany(r => Mp3InstrumentSet.Parse(r.Moods)).Distinct().ToList();
            insts = rows.SelectMany(r => Mp3InstrumentSet.Parse(r.Instruments)).Distinct().ToList();
            existingTitles = db.Mp3MoodCards.Select(c => c.Title).ToList();
        }

        await _chatLock.WaitAsync(ct);
        string reply;
        try
        {
            await using var session = LlmGateway.OpenSession();
            var prompt =
                "너는 음악 추천 큐레이터야. 아래 로컬 음악 라이브러리의 태그 목록에서 '느낌 카드' 하나를 만들어.\n" +
                "카드는 분위기 테마 하나를 잡고, 그 테마에 어울리는 장르/가수/느낌/악기 필터를 고르는 것.\n" +
                "반드시 아래 목록에 실제로 있는 값만 사용해. 각 항목은 없으면 '없음'.\n" +
                "다음 형식 그대로 6줄만 답해. 다른 말은 절대 쓰지 마:\n" +
                "제목: <짧은 카드 이름>\n설명: <포함된 가수와 느낌을 1~2문장으로 요약>\n" +
                "장르: <쉼표 목록 또는 없음>\n가수: <쉼표 목록 또는 없음>\n" +
                "느낌: <쉼표 목록 또는 없음>\n악기: <쉼표 목록 또는 없음>\n\n" +
                "[라이브러리]\n" +
                $"장르: {string.Join(", ", cats)}\n" +
                $"가수: {string.Join(", ", artists)}\n" +
                $"느낌: {string.Join(", ", moods)}\n" +
                $"악기: {string.Join(", ", insts)}\n" +
                (existingTitles.Count > 0
                    ? $"이미 있는 카드(테마가 겹치지 않게): {string.Join(", ", existingTitles)}\n"
                    : "");
            reply = ((await session.SendAsync(prompt, ct)) ?? "").Trim();
        }
        catch (OperationCanceledException) { return new { ok = false, error = "cancelled" }; }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Mp3] card create failed: {ex.GetType().Name}: {ex.Message}");
            return new { ok = false, error = ex.Message };
        }
        finally { _chatLock.Release(); }

        var title = CardLine(reply, "제목");
        var desc  = CardLine(reply, "설명");
        var fCats  = ClampList(CardLine(reply, "장르"), cats);
        var fArts  = ClampArtists(CardLine(reply, "가수"), artists);
        var fMoods = ClampList(CardLine(reply, "느낌"), moods);
        var fInsts = ClampList(CardLine(reply, "악기"), insts);
        if (title.Length == 0 || (fCats.Count + fArts.Count + fMoods.Count + fInsts.Count) == 0)
        {
            AppLogger.Log($"[WebDev:Mp3] card parse failed | raw='{Trunc(reply, 120)}'");
            return new { ok = false, error = "parse-failed", raw = Trunc(reply, 200) };
        }

        var filtersJson = JsonSerializer.Serialize(new
        {
            categories = fCats, artists = fArts, moods = fMoods, instruments = fInsts,
        });
        using (var db = new AppDbContext())
        {
            var card = new Mp3MoodCard { Title = title, Description = desc, FiltersJson = filtersJson };
            db.Mp3MoodCards.Add(card);
            db.SaveChanges();
            AppLogger.Log($"[WebDev:Mp3] card created | '{title}' filters={filtersJson}");
            return new { ok = true, card = new Mp3CardDto(card.Id, card.Title, card.Description, card.FiltersJson, card.CreatedAtUtc) };
        }
    }

    private static string CardLine(string reply, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            reply, $@"^\s*{key}\s*[:：]\s*(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline);
        var v = m.Success ? m.Groups[1].Value.Trim() : "";
        return v == "없음" ? "" : v;
    }

    private static List<string> ClampList(string csv, IReadOnlyList<string> inventory)
        => csv.Split(',', '、')
            .Select(s => s.Trim()).Where(s => s.Length > 0)
            .Select(s => inventory.FirstOrDefault(i => string.Equals(i, s, StringComparison.OrdinalIgnoreCase)))
            .Where(s => s is not null).Select(s => s!)
            .Distinct().Take(8).ToList();

    // 아티스트는 표기가 흔들리므로 (피처링/괄호) 부분일치까지 허용해 인벤토리
    // 표기로 정규화한다.
    private static List<string> ClampArtists(string csv, IReadOnlyList<string> inventory)
        => csv.Split(',', '、')
            .Select(s => s.Trim()).Where(s => s.Length > 1)
            .Select(s => inventory.FirstOrDefault(i =>
                string.Equals(i, s, StringComparison.OrdinalIgnoreCase)
                || i.Contains(s, StringComparison.OrdinalIgnoreCase)
                || s.Contains(i, StringComparison.OrdinalIgnoreCase)))
            .Where(s => s is not null).Select(s => s!)
            .Distinct().Take(8).ToList();

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
        long lastBatchMs = 0;
        var batch = new List<Mp3TrackDto>(128);

        void EmitProgress(string phase, string current, bool force = false)
        {
            var now = Environment.TickCount64;
            if (!force && now - lastEmitMs < 250) return;
            lastEmitMs = now;
            _mp3LastProgress = new Mp3ScanProgressInfo(phase, done, total, current, added, updated, classified);
            Mp3ScanProgress?.Invoke(_mp3LastProgress);
        }

        // 트랙 이벤트 배치 (M0030 후속#1) — 2s 또는 100건마다 한 번만 UI로.
        void QueueTrack(Mp3TrackDto dto, bool force = false)
        {
            batch.Add(dto);
            var now = Environment.TickCount64;
            if (!force && batch.Count < 100 && now - lastBatchMs < 2000) return;
            lastBatchMs = now;
            var snapshot = batch.ToList();
            batch.Clear();
            Mp3TrackBatch?.Invoke(snapshot);
        }
        void FlushTracks()
        {
            if (batch.Count == 0) return;
            var snapshot = batch.ToList();
            batch.Clear();
            Mp3TrackBatch?.Invoke(snapshot);
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
                // (batched) event while the scan keeps running (M0029 확장2).
                QueueTrack(ToDto(row, root));
                EmitProgress("scan", f.Title);

                if (needsClassify && categories.Count > 0)
                    backlog.Add((row.Id, f, row.Category.Length > 0));
            }
            FlushTracks();

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
                        QueueTrack(ToDto(again, root));
                    }
                }
                EmitProgress("classify", f.Title);
            }
            FlushTracks();
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
        // 후속#4 — File.Exists를 여기서 돌리지 않는다: 5k+ 라이브러리에서
        // 목록 조회의 주 병목이었다. 삭제된 파일은 재생 시점에 미디어
        // 오류로 드러나고 플러그인이 그 곡을 비활성 마킹 + 다음 곡으로
        // 넘어간다.
        var rel = Mp3Scanner.ToRelativePath(currentRoot, r.FilePath);
        bool available = rel.Length > 0;
        return new Mp3TrackDto(
            r.Id, rel, r.FileName, r.FolderName, r.Title, r.Artist, r.Album, r.TagGenre,
            r.Category, r.CategoryBy, r.VocalGender, r.Instruments, r.Moods, r.DurationSeconds,
            r.PlayCount, r.LastPlayedAtUtc, available);
    }
}
