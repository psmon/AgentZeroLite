using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace AgentZeroWpf;

// =========================================================================
//  날짜 인식 및 범위 필터링 헬퍼
// =========================================================================

internal static class DateMatchHelper
{
    private static readonly Regex KoreanFullDateRx = new(@"^\s*(\d{4})년\s*(\d{1,2})월\s*(\d{1,2})일", RegexOptions.Compiled);
    private static readonly Regex IsoDateRx        = new(@"^\s*(\d{4})-(\d{1,2})-(\d{1,2})", RegexOptions.Compiled);
    private static readonly Regex MonthDayTimeRx   = new(@"^\s*(\d{1,2})-(\d{1,2})\s+오[전후]", RegexOptions.Compiled);
    private static readonly Regex RelativeDateRx   = new(@"^\s*(어제|오늘)\s+오[전후]", RegexOptions.Compiled);
    private static readonly Regex KoreanShortDateRx = new(@"^\s*(\d{1,2})월\s*(\d{1,2})일", RegexOptions.Compiled);

    /// <summary>라인에서 날짜 패턴을 추출한다. 인식 불가 시 null.</summary>
    public static DateTime? ExtractDate(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        // 1. 한글 전체: 2026년 2월 21일
        var m = KoreanFullDateRx.Match(line);
        if (m.Success && TryBuildDate(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), out var d1))
            return d1;

        // 2. ISO: 2025-12-28
        m = IsoDateRx.Match(line);
        if (m.Success && TryBuildDate(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), out var d2))
            return d2;

        // 3. MM-DD + 시간: 02-03 오후
        m = MonthDayTimeRx.Match(line);
        if (m.Success && TryBuildDate(DateTime.Today.Year, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), out var d3))
            return d3;

        // 4. 상대 날짜: 어제/오늘 오후
        m = RelativeDateRx.Match(line);
        if (m.Success)
        {
            return m.Groups[1].Value switch
            {
                "오늘" => DateTime.Today,
                "어제" => DateTime.Today.AddDays(-1),
                _ => null
            };
        }

        // 5. 한글 축약: 2월 21일 (연도 없음 → 올해)
        m = KoreanShortDateRx.Match(line);
        if (m.Success && TryBuildDate(DateTime.Today.Year, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), out var d5))
            return d5;

        return null;
    }

    private static bool TryBuildDate(int y, int mo, int d, out DateTime result)
    {
        result = default;
        if (mo < 1 || mo > 12 || d < 1 || d > 31) return false;
        try { result = new DateTime(y, mo, d); return true; }
        catch { return false; }
    }

    public static bool IsInRange(DateTime date, DateTime? start, DateTime? end)
    {
        if (start.HasValue && date.Date < start.Value.Date) return false;
        if (end.HasValue && date.Date > end.Value.Date) return false;
        return true;
    }

    /// <summary>
    /// 새로 수집된 라인 중 시작일 이전 날짜가 충분히 감지되면 true → 스크롤 중지.
    /// 짧은 "순수 날짜 라인"(60자 미만)만 검사하여 메시지 본문 내 옛날 날짜에 오탐 방지.
    /// Teams는 같은 메시지에 한글+MM-DD 2줄을 표시하므로, 최소 3건(= 2개 이상 메시지)이어야 중지.
    /// </summary>
    public static bool ShouldStopScrolling(IEnumerable<string> newLines, DateTime startDate)
    {
        int beforeCount = 0;
        DateTime? firstBefore = null;
        foreach (var line in newLines)
        {
            // 긴 라인은 메시지 본문/요약 → 순수 날짜 마커가 아님
            if (line.Length > 60) continue;

            var date = ExtractDate(line);
            if (date.HasValue && date.Value.Date < startDate.Date)
            {
                beforeCount++;
                firstBefore ??= date.Value;
            }
        }

        if (beforeCount > 0)
            AppLogger.Log($"[Filter] 시작일 이전 날짜 {beforeCount}건 감지 (첫: {firstBefore:yyyy-MM-dd}), 중지 threshold=3");

        return beforeCount >= 3;
    }

    /// <summary>
    /// 텍스트를 날짜 컨텍스트 기반으로 필터링한다.
    /// 날짜 라인이 등장하면 그 이후 행은 해당 날짜 소속.
    /// 범위 내 날짜 소속 행만 반환하며, 날짜 미지정 행(첫 날짜 등장 전)은 제외.
    /// Teams 등에서 같은 메시지가 한글+MM-DD 두 줄로 나와도 자연스럽게 처리됨.
    /// </summary>
    public static string FilterByDateRange(string text, DateTime? start, DateTime? end)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd('\r');

        // Pass 1: 각 라인에 날짜 컨텍스트 부여
        var lineDate = new DateTime?[lines.Length];
        DateTime? current = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var d = ExtractDate(lines[i]);
            if (d.HasValue) current = d;
            lineDate[i] = current;
        }

        // 날짜를 하나도 못 찾은 경우
        if (current == null)
        {
            AppLogger.Log("[Filter] 날짜 패턴 없음 → 전체 제외");
            return string.Empty;
        }

        // Pass 2: 범위 내 라인 마킹
        var include = new bool[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            if (lineDate[i] is { } ld && IsInRange(ld, start, end))
                include[i] = true;
        }

        // Pass 3: 발신자 라인 포함 (날짜 라인 직전의 비-날짜/비-공백 라인)
        for (int i = 1; i < lines.Length; i++)
        {
            if (include[i] && ExtractDate(lines[i]) != null && !include[i - 1]
                && !string.IsNullOrWhiteSpace(lines[i - 1]) && ExtractDate(lines[i - 1]) == null)
            {
                include[i - 1] = true;
            }
        }

        // 결과 빌드
        var result = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (include[i])
                result.Add(lines[i]);
        }

        // 후행 빈 줄 제거
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
            result.RemoveAt(result.Count - 1);

        string filtered = string.Join(Environment.NewLine, result);
        AppLogger.Log($"[Filter] 날짜 범위 필터 적용: {text.Length}자 → {filtered.Length}자 (범위: {start?.ToString("yyyy-MM-dd") ?? "∞"}~{end?.ToString("yyyy-MM-dd") ?? "∞"})");
        return filtered;
    }
}

internal sealed partial class TextCaptureService
{
    private readonly ChromiumTextCapture _chromium = new();

    public async Task<string> CaptureAsync(IntPtr hwnd, CancellationToken ct, IProgress<string>? progress = null,
        ScrapWriter? scrap = null, Func<bool>? askContinue = null, ScrollOptions? scrollOptions = null,
        NativeMethods.POINT? pickPoint = null, IntPtr childHwnd = default)
    {
        var scroll = scrollOptions ?? new ScrollOptions();
        string raw = await CaptureCoreAsync(hwnd, ct, progress, scrap, askContinue, scroll, pickPoint, childHwnd);
        string cleaned = CleanCapturedText(raw);

        bool hasFilter = scroll.FilterStartDate.HasValue || scroll.FilterEndDate.HasValue;
        if (hasFilter)
        {
            progress?.Report("날짜 범위 필터 적용 중...");
            cleaned = DateMatchHelper.FilterByDateRange(cleaned, scroll.FilterStartDate, scroll.FilterEndDate);
        }

        return cleaned;
    }

    private static string CleanCapturedText(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;

        string cleaned = raw.Replace("\uFFFC", "");

        var lines = cleaned.Split('\n');
        var result = new List<string>(lines.Length);

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line))
            {
                result.Add("");
                continue;
            }

            if (OnlySymbolsRegex().IsMatch(line))
                continue;

            result.Add(line);
        }

        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
            result.RemoveAt(result.Count - 1);

        string final = string.Join(Environment.NewLine, result);
        if (final.Length != raw.Length)
            AppLogger.Log($"[Clean] 텍스트 정리: {raw.Length}자 → {final.Length}자");
        return final;
    }

    [GeneratedRegex(@"^[\s\p{S}\p{P}\p{Co}\uFFFC]{0,2}$")]
    private static partial Regex OnlySymbolsRegex();

    private async Task<string> CaptureCoreAsync(IntPtr hwnd, CancellationToken ct, IProgress<string>? progress, ScrapWriter? scrap, Func<bool>? askContinue, ScrollOptions scroll, NativeMethods.POINT? pickPoint, IntPtr childHwnd)
    {
        string className = GetClassName(hwnd);
        string windowTitle = GetWindowTitle(hwnd);
        bool isKakao = IsKakaoWindow(className);
        AppLogger.Log($"[Capture] 시작 | hwnd=0x{hwnd:X8}, child=0x{childHwnd:X8}, class=\"{className}\", kakao={isKakao}, title=\"{windowTitle}\"");

        string bestPartial = string.Empty;

        try
        {
            // KakaoTalk: native control → Clipboard 전략 우선 시도
            if (isKakao)
            {
                progress?.Report("카카오톡 감지 → 클립보드 캡처 시도 중...");
                AppLogger.Log("[Capture] 카카오톡 감지 → 클립보드 캡처 전략");

                string? kakaoResult = await Task.Run(() =>
                    TryKakaoClipboardCapture(hwnd, childHwnd, ct, progress, scrap, scroll, pickPoint), CancellationToken.None);

                if (!string.IsNullOrWhiteSpace(kakaoResult))
                {
                    AppLogger.Log($"[Capture] 카카오톡 클립보드 캡처 성공 | {kakaoResult.Length}자");
                    scrap?.WriteAll(kakaoResult);
                    progress?.Report($"카카오톡 캡처 완료 ({kakaoResult.Length}자)");
                    return kakaoResult;
                }

                if (ct.IsCancellationRequested)
                {
                    AppLogger.Log("[Capture] 카카오톡 캡처 취소됨 — 빈 결과");
                    return bestPartial;
                }

                AppLogger.Log("[Capture] 카카오톡 클립보드 캡처 실패 → 기존 전략으로 전환");
                progress?.Report("클립보드 캡처 실패 → 기존 전략 시도...");
            }

            if (ChromiumTextCapture.IsChromiumWindow(className))
            {
                progress?.Report($"Chromium 창 감지: {className}");
                AppLogger.Log($"[Capture] Chromium 창 감지 → ChromiumTextCapture 위임");

                string? chromiumResult = await _chromium.TryCaptureAsync(hwnd, windowTitle, ct, progress, scrap, askContinue, scroll, pickPoint);
                if (!string.IsNullOrWhiteSpace(chromiumResult))
                {
                    if (chromiumResult.Length > bestPartial.Length)
                        bestPartial = chromiumResult;

                    if (!ct.IsCancellationRequested)
                        return chromiumResult;
                }

                if (ct.IsCancellationRequested)
                {
                    AppLogger.Log($"[Capture] 취소됨 — 부분 결과 반환 ({bestPartial.Length}자)");
                    return bestPartial;
                }

                AppLogger.Log("[Capture] Chromium 전략 모두 실패 → 기존 전략으로 전환");
                progress?.Report("Chromium 전용 전략 실패 → 기존 전략으로 전환...");
            }
            else
            {
                AppLogger.Log("[Capture] 일반 창 → 기존 전략 사용");
            }

            ct.ThrowIfCancellationRequested();

            progress?.Report("TextPattern으로 시도 중...");
            AppLogger.Log("[Capture] Strategy 1: TextPattern");
            string? text = await Task.Run(() => TryTextPattern(hwnd, ct), ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                AppLogger.Log($"[Capture] TextPattern 성공 | {text.Length}자");
                scrap?.WriteAll(text);
                progress?.Report("TextPattern으로 캡처 완료");
                return text;
            }
            AppLogger.Log("[Capture] TextPattern 실패");

            ct.ThrowIfCancellationRequested();

            progress?.Report("스크롤 영역 포커싱 캡처 시도 중...");
            AppLogger.Log("[Capture] Strategy 2: FocusedAreaCapture");
            text = await Task.Run(() => TryFocusedAreaCapture(hwnd, ct, progress, scroll, pickPoint), ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                AppLogger.Log($"[Capture] FocusedAreaCapture 성공 | {text.Length}자");
                scrap?.WriteAll(text);
                progress?.Report("FocusedAreaCapture로 캡처 완료");
                return text;
            }
            AppLogger.Log("[Capture] FocusedAreaCapture 실패");

            ct.ThrowIfCancellationRequested();

            // Strategy 2.5 — Clipboard-driven scroll capture (M0019 follow-up #2).
            // Works for any UI that supports Ctrl+A/C/Home/End/PageDown — including
            // Swing/AWT (IntelliJ) and most browsers/editors where UIA can't see
            // the scrollable inner panes. Emits chunks per iteration so the
            // ScrapPagePanel preview fills LIVE as the scroll advances.
            progress?.Report("키보드+클립보드 스크롤 캡처 시도 중...");
            AppLogger.Log("[Capture] Strategy 2.5: ClipboardScroll");
            text = await Task.Run(() => TryClipboardScrollCapture(hwnd, ct, progress, scrap, scroll), ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                AppLogger.Log($"[Capture] ClipboardScroll 성공 | {text.Length}자");
                // NOTE: TryClipboardScrollCapture already emitted chunks via
                // scrap.WriteAll per iteration, so DON'T write the full text
                // again here — that would double-fire ChunkWritten and append
                // the entire content a second time to the preview pane.
                progress?.Report("ClipboardScroll로 캡처 완료");
                return text;
            }
            AppLogger.Log("[Capture] ClipboardScroll 실패");

            ct.ThrowIfCancellationRequested();

            progress?.Report("ScrollPattern + 트리 순회 시도 중...");
            AppLogger.Log("[Capture] Strategy 3: ScrollPattern + TreeWalk");
            text = await Task.Run(() => TryScrollPatternTreeWalk(hwnd, ct, progress, scroll), ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                AppLogger.Log($"[Capture] ScrollPattern 성공 | {text.Length}자");
                scrap?.WriteAll(text);
                progress?.Report("ScrollPattern으로 캡처 완료");
                return text;
            }
            AppLogger.Log("[Capture] ScrollPattern 실패");

            ct.ThrowIfCancellationRequested();

            progress?.Report("WM_VSCROLL 폴백 시도 중...");
            AppLogger.Log("[Capture] Strategy 4: WM_VSCROLL");
            text = await Task.Run(() => TryWmVScrollFallback(hwnd, ct, progress), ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                AppLogger.Log($"[Capture] WM_VSCROLL 성공 | {text.Length}자");
                scrap?.WriteAll(text);
                progress?.Report("WM_VSCROLL로 캡처 완료");
                return text;
            }
            AppLogger.Log("[Capture] 모든 전략 실패");

            progress?.Report("텍스트를 캡처할 수 없습니다");
            return string.Empty;
        }
        catch (OperationCanceledException)
        {
            AppLogger.Log($"[Capture] 취소됨 — 부분 결과 반환 ({bestPartial.Length}자)");
            return bestPartial;
        }
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buf = new char[256];
        int len = NativeMethods.GetClassName(hwnd, buf, buf.Length);
        return len > 0 ? new string(buf, 0, len) : string.Empty;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int len = NativeMethods.GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var buf = new char[len + 1];
        NativeMethods.GetWindowText(hwnd, buf, buf.Length);
        return new string(buf, 0, len);
    }

    private static string? TryFocusedAreaCapture(IntPtr hwnd, CancellationToken ct, IProgress<string>? progress, ScrollOptions scroll, NativeMethods.POINT? pickPoint)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);

            var scrollCondition = new PropertyCondition(
                AutomationElement.IsScrollPatternAvailableProperty, true);
            var scrollables = root.FindAll(TreeScope.Descendants, scrollCondition);

            if (scrollables.Count == 0)
            {
                AppLogger.Log("[FocusedArea] 스크롤 가능 자식 영역 없음");
                return null;
            }

            AppLogger.Log($"[FocusedArea] 스크롤 가능 자식 영역 {scrollables.Count}개 발견");

            var candidates = new List<(AutomationElement Element, System.Windows.Rect Bounds, double Area)>();
            foreach (AutomationElement el in scrollables)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var bounds = el.Current.BoundingRectangle;
                    if (bounds.IsEmpty || bounds.Width < 100 || bounds.Height < 100)
                        continue;

                    double area = bounds.Width * bounds.Height;
                    string name = el.Current.Name ?? "(unnamed)";
                    string controlType = el.Current.ControlType?.ProgrammaticName ?? "unknown";
                    AppLogger.Log($"[FocusedArea]   영역: {controlType} \"{name}\" | {bounds.Width:F0}x{bounds.Height:F0} (area={area:F0})");
                    candidates.Add((el, bounds, area));
                }
                catch { }
            }

            if (candidates.Count == 0)
            {
                AppLogger.Log("[FocusedArea] 유효한 후보 영역 없음 (모두 너무 작음)");
                return null;
            }

            // 픽포인트가 포함된 영역을 우선 선택
            if (pickPoint.HasValue)
            {
                var pp = pickPoint.Value;
                var matched = candidates.FindAll(c =>
                    pp.X >= c.Bounds.Left && pp.X <= c.Bounds.Right &&
                    pp.Y >= c.Bounds.Top && pp.Y <= c.Bounds.Bottom);

                if (matched.Count > 0)
                {
                    // 픽포인트를 포함하는 영역 중 가장 작은 것 (가장 구체적인 영역)
                    matched.Sort((a, b) => a.Area.CompareTo(b.Area));
                    candidates = matched;
                    AppLogger.Log($"[FocusedArea] 픽포인트({pp.X},{pp.Y}) 포함 영역 {matched.Count}개 → 해당 영역만 처리");
                }
                else
                {
                    AppLogger.Log($"[FocusedArea] 픽포인트({pp.X},{pp.Y}) 포함 영역 없음 → 면적순 폴백");
                    candidates.Sort((a, b) => b.Area.CompareTo(a.Area));
                }
            }
            else
            {
                candidates.Sort((a, b) => b.Area.CompareTo(a.Area));
            }

            string? bestResult = null;
            string bestRegionInfo = "";

            foreach (var (element, bounds, area) in candidates)
            {
                if (ct.IsCancellationRequested)
                {
                    AppLogger.Log($"[FocusedArea] 취소됨 — 부분 결과 반환 ({bestResult?.Length ?? 0}자)");
                    break;
                }

                string regionName = element.Current.Name ?? "(unnamed)";
                string regionType = element.Current.ControlType?.ProgrammaticName ?? "unknown";
                AppLogger.Log($"[FocusedArea] 영역 처리 시작: {regionType} \"{regionName}\"");

                try
                {
                    FocusElement(element, bounds);
                    Thread.Sleep(200);

                    ScrollPattern? scrollPattern = null;
                    if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out object? spObj))
                    {
                        scrollPattern = (ScrollPattern)spObj;
                        if (scrollPattern.Current.VerticallyScrollable)
                        {
                            try
                            {
                                scrollPattern.SetScrollPercent(ScrollPattern.NoScroll, 0);
                                Thread.Sleep(100);
                            }
                            catch { }
                        }
                    }

                    // 픽포인트가 이 영역 안에 있으면 그 위치 사용, 아니면 중앙
                    int scrollX, scrollY;
                    if (pickPoint.HasValue &&
                        pickPoint.Value.X >= bounds.Left && pickPoint.Value.X <= bounds.Right &&
                        pickPoint.Value.Y >= bounds.Top && pickPoint.Value.Y <= bounds.Bottom)
                    {
                        scrollX = pickPoint.Value.X;
                        scrollY = pickPoint.Value.Y;
                        AppLogger.Log($"[FocusedArea] 스크롤 위치: 픽포인트 ({scrollX},{scrollY})");
                    }
                    else
                    {
                        scrollX = (int)(bounds.Left + bounds.Width / 2);
                        scrollY = (int)(bounds.Top + bounds.Height / 2);
                    }
                    NativeMethods.SetCursorPos(scrollX, scrollY);
                    Thread.Sleep(50);

                    IntPtr fgWindow = NativeMethods.GetForegroundWindow();

                    var collected = new HashSet<string>();
                    var lines = new List<string>();
                    int maxScrollAttempts = scroll.MaxAttempts;
                    const int noNewTextTimeoutMs = 20_000;
                    const int pollIntervalMs = 300;
                    int consecutiveNoNewText = 0;

                    CollectVisibleText(element, collected, lines);
                    AppLogger.Log($"[FocusedArea] 영역 \"{regionName}\" 초기 수집: {lines.Count}줄");

                    for (int scrollAttempts = 0; scrollAttempts < maxScrollAttempts; scrollAttempts++)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            AppLogger.Log($"[FocusedArea] 영역 \"{regionName}\" 취소됨 — 부분 결과 ({lines.Count}줄)");
                            break;
                        }

                        if (NativeMethods.GetForegroundWindow() != fgWindow)
                        {
                            AppLogger.Log($"[FocusedArea] 영역 \"{regionName}\" 포커스 상실 → 스크롤 종료");
                            break;
                        }

                        double scrollBefore = -1;
                        try { if (scrollPattern != null) scrollBefore = scrollPattern.Current.VerticalScrollPercent; }
                        catch { }

                        NativeMethods.mouse_event(
                            NativeMethods.MOUSEEVENTF_WHEEL, 0, 0,
                            -NativeMethods.WHEEL_DELTA * scroll.DeltaMultiplier, IntPtr.Zero);
                        Thread.Sleep(scroll.DelayMs);

                        double scrollAfter = -1;
                        try { if (scrollPattern != null) scrollAfter = scrollPattern.Current.VerticalScrollPercent; }
                        catch { }

                        bool scrollTrackable = scrollBefore >= 0 && scrollAfter >= 0;
                        bool scrollMoved = scrollTrackable && Math.Abs(scrollAfter - scrollBefore) >= 0.1;
                        bool atBottom = scrollTrackable && scrollAfter >= 99.5;

                        int linesBefore = lines.Count;
                        CollectVisibleText(element, collected, lines);
                        bool gotNewText = lines.Count > linesBefore;

                        // 날짜 범위 필터: 새 텍스트에서 시작일 이전 날짜 감지 → 스크롤 중지
                        if (gotNewText && scroll.FilterStartDate.HasValue)
                        {
                            var newLines = lines.GetRange(linesBefore, lines.Count - linesBefore);
                            if (DateMatchHelper.ShouldStopScrolling(newLines, scroll.FilterStartDate.Value))
                            {
                                AppLogger.Log($"[Filter] 시작일({scroll.FilterStartDate.Value:yyyy-MM-dd}) 이전 날짜 감지 → 스크롤 중지");
                                break;
                            }
                        }

                        if (gotNewText)
                        {
                            consecutiveNoNewText = 0;
                        }
                        else
                        {
                            consecutiveNoNewText++;
                        }

                        if (scrollTrackable)
                        {
                            if (scrollMoved && !atBottom)
                            {
                                if (scrollAttempts % 5 == 0)
                                    progress?.Report($"영역 \"{regionName}\" 스크롤 중... ({scrollAttempts}, {lines.Count}줄, {scrollAfter:F0}%)");
                                continue;
                            }
                            AppLogger.Log($"[FocusedArea] 영역 \"{regionName}\" 스크롤 끝 ({scrollAfter:F1}%) → {noNewTextTimeoutMs}ms 최종 대기");
                        }
                        else
                        {
                            if (gotNewText)
                            {
                                if (scrollAttempts % 5 == 0)
                                    progress?.Report($"영역 \"{regionName}\" 스크롤 중... ({scrollAttempts}, {lines.Count}줄)");
                                continue;
                            }
                            if (consecutiveNoNewText < 5)
                            {
                                AppLogger.Log($"[FocusedArea] 영역 \"{regionName}\" 위치 추적 불가, 계속 스크롤 ({consecutiveNoNewText}/5)");
                                continue;
                            }
                            AppLogger.Log($"[FocusedArea] 영역 \"{regionName}\" 연속 {consecutiveNoNewText}회 새 텍스트 없음 → {noNewTextTimeoutMs}ms 최종 대기");
                        }

                        var deadline = Environment.TickCount64 + noNewTextTimeoutMs;
                        bool gotLateContent = false;
                        while (Environment.TickCount64 < deadline)
                        {
                            if (ct.IsCancellationRequested) break;
                            Thread.Sleep(pollIntervalMs);
                            int lb = lines.Count;
                            CollectVisibleText(element, collected, lines);
                            if (lines.Count > lb)
                            {
                                AppLogger.Log($"[FocusedArea] 대기 중 +{lines.Count - lb}줄");
                                gotLateContent = true;
                            }
                            if (NativeMethods.GetForegroundWindow() != fgWindow)
                                break;
                        }

                        if (gotLateContent && !scrollTrackable)
                        {
                            consecutiveNoNewText = 0;
                            AppLogger.Log($"[FocusedArea] 영역 \"{regionName}\" 대기 중 새 콘텐츠 발견 → 스크롤 재개");
                            continue;
                        }

                        AppLogger.Log($"[FocusedArea] 영역 \"{regionName}\" 스크롤 종료 (attempt={scrollAttempts})");
                        break;
                    }

                    string result = string.Join(Environment.NewLine, lines);
                    AppLogger.Log($"[FocusedArea] 영역 \"{regionName}\" 수집 완료 | {result.Length}자, {lines.Count}줄");

                    if (result.Length > (bestResult?.Length ?? 0))
                    {
                        bestResult = result;
                        bestRegionInfo = $"{regionType} \"{regionName}\"";
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLogger.Log($"[FocusedArea] 영역 \"{regionName}\" 처리 실패: {ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(bestResult))
            {
                AppLogger.Log($"[FocusedArea] 최종 선택 영역: {bestRegionInfo} | {bestResult.Length}자");
                return bestResult;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Log($"[FocusedArea] 전략 실패: {ex.Message}");
        }

        return null;
    }

    private static void FocusElement(AutomationElement element, System.Windows.Rect bounds)
    {
        try
        {
            element.SetFocus();
            AppLogger.Log("[FocusedArea]   포커스: SetFocus 성공");
            return;
        }
        catch
        {
            AppLogger.Log("[FocusedArea]   포커스: SetFocus 실패 → 물리 클릭 폴백");
        }

        try
        {
            int centerX = (int)(bounds.Left + bounds.Width / 2);
            int centerY = (int)(bounds.Top + bounds.Height / 2);
            NativeMethods.SetCursorPos(centerX, centerY);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
            AppLogger.Log($"[FocusedArea]   포커스: 물리 클릭 ({centerX}, {centerY})");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[FocusedArea]   포커스: 물리 클릭 실패: {ex.Message}");
        }
    }

    private static string? TryTextPattern(IntPtr hwnd, CancellationToken ct)
    {
        try
        {
            var element = AutomationElement.FromHandle(hwnd);
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out object? pattern))
            {
                ct.ThrowIfCancellationRequested();
                var textPattern = (TextPattern)pattern;
                var range = textPattern.DocumentRange;
                return range.GetText(-1);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return null;
    }

    private static string? TryScrollPatternTreeWalk(IntPtr hwnd, CancellationToken ct, IProgress<string>? progress, ScrollOptions scroll)
    {
        try
        {
            var element = AutomationElement.FromHandle(hwnd);
            ScrollPattern? scrollPattern = null;

            if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out object? sp))
                scrollPattern = (ScrollPattern)sp;

            if (scrollPattern?.Current.VerticallyScrollable == true)
            {
                scrollPattern.SetScrollPercent(ScrollPattern.NoScroll, 0);
                Thread.Sleep(100);
            }

            var collected = new HashSet<string>();
            var lines = new List<string>();
            int scrollAttempts = 0;
            int maxScrollAttempts = scroll.MaxAttempts;

            while (scrollAttempts < maxScrollAttempts)
            {
                ct.ThrowIfCancellationRequested();

                int linesBefore = lines.Count;
                int newCount = CollectVisibleText(element, collected, lines);

                // 날짜 범위 필터: 시작일 이전 날짜 감지 → 스크롤 중지
                if (newCount > 0 && scroll.FilterStartDate.HasValue)
                {
                    var newLines = lines.GetRange(linesBefore, lines.Count - linesBefore);
                    if (DateMatchHelper.ShouldStopScrolling(newLines, scroll.FilterStartDate.Value))
                    {
                        AppLogger.Log($"[Filter] 시작일({scroll.FilterStartDate.Value:yyyy-MM-dd}) 이전 날짜 감지 → 스크롤 중지");
                        break;
                    }
                }

                if (scrollPattern?.Current.VerticallyScrollable != true)
                    break;

                double before = scrollPattern.Current.VerticalScrollPercent;
                scrollPattern.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                Thread.Sleep(100);

                double after = scrollPattern.Current.VerticalScrollPercent;
                if (Math.Abs(after - before) < 0.01 || after >= 99.9)
                {
                    CollectVisibleText(element, collected, lines);
                    break;
                }

                scrollAttempts++;
                progress?.Report($"스크롤 중... ({scrollAttempts})");
            }

            if (lines.Count > 0)
                return string.Join(Environment.NewLine, lines);
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return null;
    }

    private static int CollectVisibleText(AutomationElement root, HashSet<string> collected, List<string> lines)
    {
        int count = 0;
        var walker = TreeWalker.ContentViewWalker;
        CollectRecursive(walker, root, collected, lines, ref count, depth: 0);
        return count;
    }

    private static void CollectRecursive(TreeWalker walker, AutomationElement element,
        HashSet<string> collected, List<string> lines, ref int count, int depth)
    {
        if (depth > 30) return;

        try
        {
            string name = element.Current.Name ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(name) && collected.Add(name))
            {
                lines.Add(name);
                count++;
            }

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? vp))
            {
                string val = ((ValuePattern)vp).Current.Value ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(val) && collected.Add(val))
                {
                    lines.Add(val);
                    count++;
                }
            }

            var child = walker.GetFirstChild(element);
            while (child != null)
            {
                CollectRecursive(walker, child, collected, lines, ref count, depth + 1);
                child = walker.GetNextSibling(child);
            }
        }
        catch { }
    }

    private static string? TryWmVScrollFallback(IntPtr hwnd, CancellationToken ct, IProgress<string>? progress)
    {
        try
        {
            NativeMethods.SendMessage(hwnd, NativeMethods.WM_VSCROLL, (IntPtr)NativeMethods.SB_TOP, IntPtr.Zero);
            Thread.Sleep(100);

            var collected = new HashSet<string>();
            var lines = new List<string>();

            for (int i = 0; i < 200; i++)
            {
                ct.ThrowIfCancellationRequested();

                string text = GetWindowTextWm(hwnd);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    foreach (var line in text.Split('\n'))
                    {
                        string trimmed = line.TrimEnd('\r');
                        if (collected.Add(trimmed))
                            lines.Add(trimmed);
                    }
                }

                NativeMethods.SendMessage(hwnd, NativeMethods.WM_VSCROLL, (IntPtr)NativeMethods.SB_PAGEDOWN, IntPtr.Zero);
                Thread.Sleep(50);

                string textAfter = GetWindowTextWm(hwnd);
                if (textAfter == text && i > 0)
                    break;

                progress?.Report($"WM_VSCROLL 스크롤 중... ({i + 1})");
            }

            if (lines.Count > 0)
                return string.Join(Environment.NewLine, lines);
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return null;
    }

    private static string GetWindowTextWm(IntPtr hwnd)
    {
        IntPtr lengthPtr = NativeMethods.SendMessage(hwnd, NativeMethods.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
        int length = (int)lengthPtr;
        if (length <= 0)
            return string.Empty;

        var buffer = new char[length + 1];
        NativeMethods.SendMessageGetText(hwnd, NativeMethods.WM_GETTEXT, (IntPtr)buffer.Length, buffer);
        return new string(buffer, 0, length);
    }

    // =========================================================================
    //  KakaoTalk Native Control Support
    // =========================================================================

    private static bool IsKakaoWindow(string className)
    {
        return className.StartsWith("EVA_Window", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 카카오톡 네이티브 컨트롤 전용 클립보드 캡처.
    /// UIA TreeWalker가 카카오톡 메시지 텍스트를 노출하지 않으므로
    /// Ctrl+A → Ctrl+C → 클립보드 읽기 방식으로 캡처한다.
    /// 스크롤 업으로 이전 메시지를 로드한 뒤 반복 캡처하여 전체 내용을 수집한다.
    /// </summary>
    private static string? TryKakaoClipboardCapture(
        IntPtr rootHwnd, IntPtr chatHwnd, CancellationToken ct,
        IProgress<string>? progress, ScrapWriter? scrap, ScrollOptions scroll,
        NativeMethods.POINT? pickPoint)
    {
        IntPtr targetHwnd = chatHwnd != IntPtr.Zero ? chatHwnd : rootHwnd;
        AppLogger.Log($"[KakaoCapture] 시작 | root=0x{rootHwnd:X8}, chat=0x{chatHwnd:X8}, target=0x{targetHwnd:X8}");

        // 1. 대상 창 포그라운드 전환 및 채팅 영역 클릭
        NativeMethods.SetForegroundWindow(rootHwnd);
        Thread.Sleep(300);

        int clickX, clickY;
        if (pickPoint.HasValue)
        {
            clickX = pickPoint.Value.X;
            clickY = pickPoint.Value.Y;
        }
        else if (chatHwnd != IntPtr.Zero && NativeMethods.GetWindowRect(chatHwnd, out var chatRect))
        {
            clickX = chatRect.Left + chatRect.Width / 2;
            clickY = chatRect.Top + chatRect.Height / 2;
        }
        else
        {
            AppLogger.Log("[KakaoCapture] 클릭 좌표를 결정할 수 없음");
            return null;
        }

        // 채팅 영역 클릭으로 포커스 확보
        NativeMethods.SetCursorPos(clickX, clickY);
        Thread.Sleep(50);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(30);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(300);
        AppLogger.Log($"[KakaoCapture] 채팅 영역 클릭 | ({clickX},{clickY})");

        ct.ThrowIfCancellationRequested();

        // 2. 초기 클립보드 캡처
        string? captured = SimulateSelectAllCopy();
        if (string.IsNullOrEmpty(captured))
        {
            AppLogger.Log("[KakaoCapture] 초기 클립보드 복사 실패 — Ctrl+A/Ctrl+C 응답 없음");
            return null;
        }
        AppLogger.Log($"[KakaoCapture] 초기 복사 성공 | {captured.Length}자");
        progress?.Report($"카카오톡 초기 캡처: {captured.Length}자");

        string bestResult = captured;

        // 3. 스크롤 업 → 재캡처 반복 (이전 대화 로드)
        int stagnantCount = 0;
        const int maxStagnant = 4;
        const int wheelBatchSize = 10; // 한 번에 보낼 휠 업 횟수

        for (int round = 0; round < scroll.MaxAttempts && stagnantCount < maxStagnant; round++)
        {
            if (ct.IsCancellationRequested)
            {
                AppLogger.Log($"[KakaoCapture] 취소됨 — 부분 결과 반환 ({bestResult.Length}자)");
                return bestResult;
            }

            // 포그라운드 확인
            if (NativeMethods.GetForegroundWindow() != rootHwnd)
            {
                AppLogger.Log("[KakaoCapture] 포커스 상실 → 재활성화");
                NativeMethods.SetForegroundWindow(rootHwnd);
                Thread.Sleep(300);
                NativeMethods.SetCursorPos(clickX, clickY);
                NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                Thread.Sleep(30);
                NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                Thread.Sleep(200);
            }

            // 마우스 휠 업 (이전 메시지 로드)
            NativeMethods.SetCursorPos(clickX, clickY);
            Thread.Sleep(50);
            for (int w = 0; w < wheelBatchSize; w++)
            {
                NativeMethods.mouse_event(
                    NativeMethods.MOUSEEVENTF_WHEEL, 0, 0,
                    NativeMethods.WHEEL_DELTA * scroll.DeltaMultiplier, IntPtr.Zero);
                Thread.Sleep(50);
            }
            Thread.Sleep(scroll.DelayMs);

            AppLogger.Log($"[KakaoCapture] 스크롤업 #{round} | wheelBatch={wheelBatchSize}, delta={scroll.DeltaMultiplier}");

            // 재캡처
            captured = SimulateSelectAllCopy();
            if (captured != null && captured.Length > bestResult.Length)
            {
                int gained = captured.Length - bestResult.Length;
                stagnantCount = 0;
                bestResult = captured;
                AppLogger.Log($"[KakaoCapture] 스크롤#{round} 확장 | +{gained}자, total={bestResult.Length}자");
                progress?.Report($"카카오톡 스크롤 캡처 중... ({bestResult.Length}자)");

                // 날짜 범위 필터: 시작일 이전 날짜 감지 → 스크롤 중지
                if (scroll.FilterStartDate.HasValue)
                {
                    var capturedLines = captured.Split('\n');
                    if (DateMatchHelper.ShouldStopScrolling(capturedLines, scroll.FilterStartDate.Value))
                    {
                        AppLogger.Log($"[Filter] 시작일({scroll.FilterStartDate.Value:yyyy-MM-dd}) 이전 날짜 감지 → 카카오 스크롤 중지");
                        break;
                    }
                }
            }
            else
            {
                stagnantCount++;
                AppLogger.Log($"[KakaoCapture] 스크롤#{round} 증가 없음 | len={bestResult.Length}, stagnant={stagnantCount}/{maxStagnant}");
            }
        }

        AppLogger.Log($"[KakaoCapture] 완료 | {bestResult.Length}자, stagnant={stagnantCount}");
        return bestResult;
    }

    /// <summary>
    /// Strategy 2.5 — Clipboard-driven scroll capture (M0019 follow-up #2).
    ///
    /// Universal fallback for any UI that supports Ctrl+A/C/Home/End/PageDown —
    /// works on Swing/AWT (IntelliJ), web pages, code editors, terminals,
    /// anywhere the UIA Scroll surface is empty but the keyboard works.
    ///
    /// Algorithm:
    ///   1. Foreground target, click on pickPoint (or window center) to focus.
    ///   2. Ctrl+Home — jump to top.
    ///   3. Loop:
    ///       a. Ctrl+A → Ctrl+C → read clipboard.
    ///       b. If clipboard text changed, emit the *new* tail via
    ///          scrap.WriteAll(newSlice) so the preview pane updates LIVE.
    ///       c. Send PageDown to advance, wait scroll.DelayMs.
    ///       d. If clipboard hasn't grown after `maxStagnant` rounds → done.
    ///   4. Restore original clipboard.
    ///
    /// Returns full accumulated text on success; null when initial Ctrl+A/C
    /// returns nothing (UI doesn't support select-all here either).
    /// </summary>
    private static string? TryClipboardScrollCapture(
        IntPtr hwnd, CancellationToken ct, IProgress<string>? progress,
        ScrapWriter? scrap, ScrollOptions scroll)
    {
        AppLogger.Log($"[ClipScroll] 시작 | hwnd=0x{hwnd:X8}");

        // Save the user's clipboard so we don't trash it on the way out.
        string? originalClipboard = ReadClipboardText();

        try
        {
            NativeMethods.SetForegroundWindow(hwnd);
            Thread.Sleep(300);

            // Click the window center so keyboard focus lands on the content area.
            if (NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                int cx = rect.Left + rect.Width / 2;
                int cy = rect.Top + rect.Height / 2;
                NativeMethods.SetCursorPos(cx, cy);
                Thread.Sleep(50);
                NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                Thread.Sleep(30);
                NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                Thread.Sleep(200);
                AppLogger.Log($"[ClipScroll] 중앙 클릭 ({cx},{cy})");
            }

            ct.ThrowIfCancellationRequested();

            // Ctrl+Home — jump to top so we capture from the beginning.
            SendCtrlKey(NativeMethods.VK_HOME);
            Thread.Sleep(scroll.DelayMs);

            // Initial capture
            string? captured = SimulateSelectAllCopy();
            if (string.IsNullOrEmpty(captured))
            {
                AppLogger.Log("[ClipScroll] 초기 Ctrl+A/C 실패 — 이 앱은 클립보드 캡처 미지원");
                return null;
            }
            AppLogger.Log($"[ClipScroll] 초기 캡처 | {captured.Length}자");
            progress?.Report($"클립보드 초기 캡처: {captured.Length}자");
            scrap?.WriteAll(captured);

            string accumulated = captured;
            int stagnantCount = 0;
            const int maxStagnant = 3;

            for (int round = 0; round < scroll.MaxAttempts && stagnantCount < maxStagnant; round++)
            {
                if (ct.IsCancellationRequested)
                {
                    AppLogger.Log($"[ClipScroll] 취소됨 — 부분 결과 ({accumulated.Length}자)");
                    return accumulated;
                }

                // Re-foreground if focus shifted (modal popups, alt-tab races).
                if (NativeMethods.GetForegroundWindow() != hwnd)
                {
                    AppLogger.Log("[ClipScroll] 포커스 상실 → 재활성화");
                    NativeMethods.SetForegroundWindow(hwnd);
                    Thread.Sleep(200);
                }

                // PageDown to advance one viewport.
                SendKey(NativeMethods.VK_NEXT);
                Thread.Sleep(scroll.DelayMs);

                // Re-select-all + copy. For editors, Ctrl+A re-selects the *whole*
                // document so the new capture supersedes the old one. For
                // chat-style apps that only expose the visible viewport, we
                // diff and append the tail.
                string? next = SimulateSelectAllCopy();
                if (string.IsNullOrEmpty(next))
                {
                    stagnantCount++;
                    AppLogger.Log($"[ClipScroll] round {round} — 클립보드 비어 있음 (stagnant {stagnantCount}/{maxStagnant})");
                    continue;
                }

                if (next.Length <= accumulated.Length)
                {
                    stagnantCount++;
                    progress?.Report($"스크롤 {round + 1} — 진전 없음 ({stagnantCount}/{maxStagnant})");
                    AppLogger.Log($"[ClipScroll] round {round} — 길이 변화 없음 ({next.Length}자, stagnant {stagnantCount}/{maxStagnant})");
                    continue;
                }

                // Real growth — emit the tail so the preview pane fills live.
                stagnantCount = 0;
                string newSlice = next.Length > accumulated.Length && next.StartsWith(accumulated, StringComparison.Ordinal)
                    ? next.Substring(accumulated.Length)
                    : "\n--- new content ---\n" + next;
                accumulated = next;
                scrap?.WriteAll(newSlice);
                progress?.Report($"스크롤 {round + 1} — 누적 {accumulated.Length}자");
                AppLogger.Log($"[ClipScroll] round {round} — +{newSlice.Length}자 (누적 {accumulated.Length}자)");
            }

            AppLogger.Log($"[ClipScroll] 완료 | {accumulated.Length}자, stagnant={stagnantCount}");
            return accumulated;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[ClipScroll] 예외 — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            // Restore the user's clipboard.
            if (!string.IsNullOrEmpty(originalClipboard))
            {
                try
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        try { System.Windows.Clipboard.SetText(originalClipboard); } catch { }
                    });
                }
                catch { /* best-effort restore */ }
            }
        }
    }

    /// <summary>Press + release one VK_ key (no modifier).</summary>
    private static void SendKey(byte vk)
    {
        NativeMethods.keybd_event(vk, 0, 0, IntPtr.Zero);
        Thread.Sleep(20);
        NativeMethods.keybd_event(vk, 0, NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);
    }

    /// <summary>Press Ctrl + one VK_ key (Ctrl held during key down/up), then release.</summary>
    private static void SendCtrlKey(byte vk)
    {
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, IntPtr.Zero);
        Thread.Sleep(20);
        NativeMethods.keybd_event(vk, 0, 0, IntPtr.Zero);
        Thread.Sleep(20);
        NativeMethods.keybd_event(vk, 0, NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);
        Thread.Sleep(150);
    }

    /// <summary>Ctrl+A → Ctrl+C를 시뮬레이션하고 클립보드 텍스트를 반환한다.</summary>
    private static string? SimulateSelectAllCopy()
    {
        // Ctrl+A (전체 선택)
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, IntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_A, 0, 0, IntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_A, 0, NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);
        Thread.Sleep(200);

        // Ctrl+C (복사)
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, IntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_C, 0, 0, IntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_C, 0, NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);
        Thread.Sleep(300);

        return ReadClipboardText();
    }

    /// <summary>Win32 API로 클립보드의 유니코드 텍스트를 읽는다 (STA 스레드 불필요).</summary>
    private static string? ReadClipboardText()
    {
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            AppLogger.Log("[KakaoCapture] OpenClipboard 실패");
            return null;
        }

        try
        {
            IntPtr handle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
            {
                AppLogger.Log("[KakaoCapture] GetClipboardData 실패 — 텍스트 데이터 없음");
                return null;
            }

            IntPtr ptr = NativeMethods.GlobalLock(handle);
            if (ptr == IntPtr.Zero)
            {
                AppLogger.Log("[KakaoCapture] GlobalLock 실패");
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }
}
