using Agent.Common.Data;
using Agent.Common.Mp3;
using Agent.Common.Vision;
using Xunit.Abstractions;

namespace ZeroCommon.Tests.Mp3;

// M0029 후속 — 실험 프로브: 스캔된 MP3의 ID3 APIC 커버를 Florence-2 OD에
// 통과시켜 성별 라벨(man/woman/girl/…)이 실제로 나오는지 관찰한다.
// "커버 비전 → 남녀 판별 → 무대 캐릭터 전환" 개선의 사전 타당성 검증용.
// 로컬 앱 DB(%LOCALAPPDATA%)와 비전 모델이 있는 머신에서만 돌고, CI/타
// 머신에서는 스킵된다. 판정 로직 자체는 결과를 보고 설계한다 — 이 프로브는
// 관찰만 하고 실패하지 않는다.
[Trait("Category", "VisionProbe")]
public sealed class Mp3CoverVisionProbeTests
{
    private readonly ITestOutputHelper _output;
    public Mp3CoverVisionProbeTests(ITestOutputHelper output) => _output = output;

    private static readonly string[] FemaleWords = { "woman", "women", "girl", "lady", "female" };
    private static readonly string[] MaleWords   = { "man", "men", "boy", "male" };

    [SkippableFact]
    public async Task Florence2_on_scanned_mp3_covers_reports_gendered_labels()
    {
        var settings = VisionSettingsStore.Load();
        var modelDir = VisionSettingsStore.ResolveModelDir(settings);
        Skip.IfNot(VisionSettingsStore.IsModelPresent(modelDir), $"Florence-2 not downloaded ({modelDir})");

        // 실 DB에서 커버가 있는 트랙을 아티스트 다양성 있게 샘플링.
        List<(string Title, string Artist, string Gender, byte[] Cover)> samples = new();
        try
        {
            using var db = new AppDbContext();
            var rows = db.Mp3Tracks
                .OrderBy(t => t.Artist).ThenBy(t => t.Id)
                .Select(t => new { t.Title, t.Artist, t.VocalGender, t.FilePath })
                .ToList();
            Skip.If(rows.Count == 0, "no scanned Mp3Tracks in the local DB — run a scan first");

            var seenArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                if (samples.Count >= 8) break;
                if (!File.Exists(r.FilePath)) continue;
                if (!seenArtists.Add(r.Artist ?? "")) continue;   // 아티스트당 1곡
                var cover = Mp3Scanner.ReadCover(r.FilePath);
                if (cover is null) continue;
                samples.Add((r.Title, r.Artist, r.VocalGender, cover.Value.Data));
            }
        }
        catch (Exception ex)
        {
            Skip.If(true, $"local DB unavailable: {ex.Message}");
        }
        Skip.If(samples.Count == 0, "no APIC covers among scanned tracks");

        await using var vision = new Florence2VisionInterpreter(settings);
        Skip.IfNot(await vision.EnsureReadyAsync(), "Florence-2 failed to load");

        // 캡션 태스크는 인터프리터가 아직 노출하지 않으므로 실험에서는 모델을
        // 직접 구동 (인터프리터와 동일한 로드 경로).
        var source = new Florence2.FlorenceModelDownloader(modelDir);
        await source.DownloadModelsAsync(_ => { }, null, default);
        using var so = new Microsoft.ML.OnnxRuntime.SessionOptions();
        var model = new Florence2.Florence2Model(source, so);

        int odGendered = 0, capGendered = 0;
        foreach (var s in samples)
        {
            var r = await vision.InterpretAsync(s.Cover);
            var labels = r.Detections.Select(d => d.Label.ToLowerInvariant()).Distinct().ToList();
            string odVerdict = GenderOf(string.Join(" ", labels));
            if (odVerdict != "none") odGendered++;

            string caption;
            using (var ms = new MemoryStream(s.Cover, writable: false))
            {
                var cap = model.Run(Florence2.TaskTypes.CAPTION, new Stream[] { ms }, "", default);
                caption = (cap is { Length: > 0 } ? cap[0]?.PureText : null) ?? "";
            }
            string capVerdict = GenderOf(caption.ToLowerInvariant());
            if (capVerdict != "none") capGendered++;

            _output.WriteLine(
                $"[OD:{odVerdict,-6}|CAP:{capVerdict,-6}] {s.Artist} - {s.Title} " +
                $"(llm={(!string.IsNullOrEmpty(s.Gender) ? s.Gender : "?")})\n" +
                $"    od  = [{string.Join(", ", labels)}]\n" +
                $"    cap = \"{caption}\"");
        }
        _output.WriteLine($"— {samples.Count} covers | OD gendered {odGendered} | CAPTION gendered {capGendered}");
    }

    private static string GenderOf(string text)
    {
        bool female = FemaleWords.Any(w => System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{w}\b"));
        bool male   = MaleWords.Any(w => System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{w}\b"));
        return female && male ? "MIXED" : female ? "FEMALE" : male ? "MALE" : "none";
    }
}
