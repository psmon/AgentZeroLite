using System.Windows.Automation;

namespace AgentZeroWpf;

internal sealed class ChromiumTextCapture
{
    private static readonly string[] ChromiumClassNames =
    [
        "Chrome_WidgetWin_1",
        "Chrome_WidgetWin_0",
        "TeamsWebView",
        "CefBrowserWindow",
        "WebView2",
    ];

    public static bool IsChromiumWindow(string className)
    {
        foreach (var cn in ChromiumClassNames)
        {
            if (className.Equals(cn, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public async Task<string?> TryCaptureAsync(
        IntPtr hwnd, string windowTitle, CancellationToken ct, IProgress<string>? progress,
        ScrapWriter? scrap = null, Func<bool>? askContinue = null, ScrollOptions? scroll = null,
        NativeMethods.POINT? pickPoint = null)
    {
        AppLogger.Log($"[Chromium] TryCaptureAsync 시작 | hwnd=0x{hwnd:X8}, title=\"{windowTitle}\"");

        progress?.Report("Chromium 감지 → Enhanced UIA 시도 중...");
        AppLogger.Log("[Enhanced UIA] 시작");
        try
        {
            var s = scroll ?? new ScrollOptions();
            string? uiaResult = await Task.Run(() => TryEnhancedUiAutomation(hwnd, ct, progress, scrap, askContinue, s, pickPoint), ct);
            if (!string.IsNullOrWhiteSpace(uiaResult))
            {
                AppLogger.Log($"[Enhanced UIA] 캡처 성공 | {uiaResult.Length}자");
                progress?.Report("Enhanced UIA로 캡처 완료");
                return uiaResult;
            }
            AppLogger.Log("[Enhanced UIA] 결과 없음");
        }
        catch (OperationCanceledException)
        {
            AppLogger.Log("[Enhanced UIA] 취소됨");
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[Enhanced UIA] 예외 발생", ex);
        }

        AppLogger.Log("[Chromium] Enhanced UIA 실패 → 기존 전략으로 폴스루");
        return null;
    }

    private static readonly ControlType[] ContentControlTypes =
    [
        ControlType.Document,
        ControlType.Edit,
    ];

    private static string? TryEnhancedUiAutomation(
        IntPtr hwnd, CancellationToken ct, IProgress<string>? progress, ScrapWriter? scrap, Func<bool>? askContinue, ScrollOptions scroll, NativeMethods.POINT? pickPoint)
    {
        AppLogger.Log($"[Enhanced UIA] 시작 | hwnd=0x{hwnd:X8}");

        var root = AutomationElement.FromHandle(hwnd);

        progress?.Report("Enhanced UIA: 콘텐츠 요소 검색 중...");
        AutomationElement? document = null;

        foreach (var controlType in ContentControlTypes)
        {
            AppLogger.Log($"[Enhanced UIA] FindFirst(ControlType.{controlType.ProgrammaticName}) 시도...");
            var condition = new PropertyCondition(
                AutomationElement.ControlTypeProperty, controlType);
            document = root.FindFirst(TreeScope.Descendants, condition);

            if (document is not null)
            {
                AppLogger.Log($"[Enhanced UIA] {controlType.ProgrammaticName} 발견!");
                break;
            }
        }

        if (document is null)
        {
            progress?.Report("Enhanced UIA: 접근성 트리 강제 구축 중...");
            AppLogger.Log("[Enhanced UIA] FindAll(TrueCondition)으로 접근성 트리 강제 구축...");
            var allElements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            AppLogger.Log($"[Enhanced UIA] FindAll 결과: {allElements.Count}개 요소");
            Thread.Sleep(500);
            ct.ThrowIfCancellationRequested();

            foreach (var controlType in ContentControlTypes)
            {
                var condition = new PropertyCondition(
                    AutomationElement.ControlTypeProperty, controlType);
                document = root.FindFirst(TreeScope.Descendants, condition);
                if (document is not null)
                {
                    AppLogger.Log($"[Enhanced UIA] 재시도: {controlType.ProgrammaticName} 발견!");
                    break;
                }
            }
        }

        if (document is not null)
        {
            var allDocs = new List<AutomationElement>();
            foreach (var controlType in ContentControlTypes)
            {
                var condition = new PropertyCondition(
                    AutomationElement.ControlTypeProperty, controlType);
                var found = root.FindAll(TreeScope.Descendants, condition);
                foreach (AutomationElement el in found)
                    allDocs.Add(el);
            }
            AppLogger.Log($"[Enhanced UIA] 콘텐츠 요소 총 {allDocs.Count}개 발견");
            foreach (var doc in allDocs)
            {
                try
                {
                    string ctName = doc.Current.ControlType.ProgrammaticName;
                    string name = doc.Current.Name ?? "";
                    AppLogger.Log($"[Enhanced UIA]   {ctName}: \"{Truncate(name, 60)}\"");
                }
                catch { }
            }
        }

        if (document is null)
        {
            AppLogger.Log("[Enhanced UIA] 콘텐츠 요소를 찾을 수 없음 — 종료");
            return null;
        }

        var allContentElements = new List<AutomationElement>();
        foreach (var controlType in ContentControlTypes)
        {
            var condition = new PropertyCondition(
                AutomationElement.ControlTypeProperty, controlType);
            var found = root.FindAll(TreeScope.Descendants, condition);
            foreach (AutomationElement el in found)
                allContentElements.Add(el);
        }
        AppLogger.Log($"[Enhanced UIA] 콘텐츠 요소 총 {allContentElements.Count}개");

        string? bestResult = null;

        foreach (var contentElem in allContentElements)
        {
            if (ct.IsCancellationRequested)
            {
                AppLogger.Log($"[Enhanced UIA] 취소됨 — 부분 결과 반환 ({bestResult?.Length ?? 0}자)");
                break;
            }

            string elemName = "";
            string elemType = "";
            try
            {
                elemName = contentElem.Current.Name ?? "";
                elemType = contentElem.Current.ControlType.ProgrammaticName;
            }
            catch { continue; }

            if (elemName.Contains("editor is not accessible", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Log("[Enhanced UIA] ⚠ VS Code 에디터 접근 불가 감지!");
                AppLogger.Log("[Enhanced UIA] → VS Code에서 Shift+Alt+F1 또는 설정에서 editor.accessibilitySupport: on");
                progress?.Report("⚠ VS Code 스크린 리더 모드 필요 (Shift+Alt+F1)");
                continue;
            }

            AppLogger.Log($"[Enhanced UIA] {elemType} 시도: \"{Truncate(elemName, 60)}\"");
            progress?.Report($"Enhanced UIA: {elemType} 텍스트 추출 중...");

            string? textPatternResult = TryTextPatternOnElement(contentElem);
            if (!string.IsNullOrWhiteSpace(textPatternResult))
            {
                AppLogger.Log($"[Enhanced UIA] TextPattern 결과 | {textPatternResult.Length}자");
                if (textPatternResult.Length > (bestResult?.Length ?? 0))
                {
                    scrap?.WriteAll(textPatternResult);
                    bestResult = textPatternResult;
                }
            }

            string? deepResult = TryDeepCollect(contentElem, ct, progress, scrap, askContinue, scroll, pickPoint);
            if (!string.IsNullOrWhiteSpace(deepResult))
            {
                AppLogger.Log($"[Enhanced UIA] DeepCollect 결과 | {deepResult.Length}자");
                if (deepResult.Length > (bestResult?.Length ?? 0))
                    bestResult = deepResult;
            }
        }

        if (!string.IsNullOrWhiteSpace(bestResult))
        {
            bool cancelled = ct.IsCancellationRequested;
            AppLogger.Log($"[Enhanced UIA] 최종 결과: {bestResult.Length}자{(cancelled ? " (취소로 부분 결과)" : "")}");
            return bestResult;
        }

        AppLogger.Log("[Enhanced UIA] 모든 콘텐츠 요소에서 텍스트 추출 실패");
        return null;
    }

    private static string? TryTextPatternOnElement(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out object? tp))
            {
                var textPattern = (TextPattern)tp;
                string text = textPattern.DocumentRange.GetText(-1);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[Enhanced UIA] TextPattern 예외", ex);
        }
        return null;
    }

    private static string? TryDeepCollect(
        AutomationElement element, CancellationToken ct, IProgress<string>? progress,
        ScrapWriter? scrap, Func<bool>? askContinue, ScrollOptions scroll, NativeMethods.POINT? pickPoint)
    {
        System.Windows.Rect bounds;
        try
        {
            bounds = element.Current.BoundingRectangle;
        }
        catch
        {
            bounds = System.Windows.Rect.Empty;
        }

        bool canMouseWheel = !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0;
        if (!canMouseWheel)
        {
            AppLogger.Log("[Enhanced UIA] DeepCollect: BoundingRectangle 없음, 스크롤 불가");
            var c = new HashSet<string>();
            var l = new List<string>();
            CollectElementText(element, c, l);
            return l.Count > 0 ? string.Join(Environment.NewLine, l) : null;
        }

        try
        {
            if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out object? sp))
            {
                var scrollPattern = (ScrollPattern)sp;
                if (scrollPattern.Current.VerticallyScrollable)
                {
                    scrollPattern.SetScrollPercent(ScrollPattern.NoScroll, 0);
                    Thread.Sleep(200);
                }
            }
        }
        catch { }

        var collected = new HashSet<string>();
        var lines = new List<string>();

        IntPtr fgWindow = NativeMethods.GetForegroundWindow();

        // 픽포인트가 이 영역 안에 있으면 그 위치 사용, 아니면 중앙
        int scrollX, scrollY;
        if (pickPoint.HasValue &&
            pickPoint.Value.X >= bounds.Left && pickPoint.Value.X <= bounds.Right &&
            pickPoint.Value.Y >= bounds.Top && pickPoint.Value.Y <= bounds.Bottom)
        {
            scrollX = pickPoint.Value.X;
            scrollY = pickPoint.Value.Y;
            AppLogger.Log($"[Enhanced UIA] DeepCollect 스크롤 위치: 픽포인트 ({scrollX},{scrollY})");
        }
        else
        {
            scrollX = (int)(bounds.Left + bounds.Width / 2);
            scrollY = (int)(bounds.Top + bounds.Height / 2);
        }
        NativeMethods.SetCursorPos(scrollX, scrollY);
        Thread.Sleep(50);

        CollectElementText(element, collected, lines);
        if (lines.Count > 0)
            scrap?.WriteLines(lines);
        AppLogger.Log($"[Enhanced UIA] DeepCollect 초기 수집: {lines.Count}줄");

        int maxScrollAttempts = scroll.MaxAttempts;
        const long noNewTextStopMs = 20_000;

        long lastNewTextTick = Environment.TickCount64;

        for (int attempt = 0; attempt < maxScrollAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
            {
                AppLogger.Log($"[Enhanced UIA] DeepCollect: 취소됨 — 부분 결과 반환 ({lines.Count}줄)");
                break;
            }

            if (NativeMethods.GetForegroundWindow() != fgWindow)
            {
                AppLogger.Log("[Enhanced UIA] DeepCollect: 포커스 상실 → 스크롤 종료");
                break;
            }

            NativeMethods.mouse_event(
                NativeMethods.MOUSEEVENTF_WHEEL, 0, 0,
                -NativeMethods.WHEEL_DELTA * scroll.DeltaMultiplier, IntPtr.Zero);
            Thread.Sleep(scroll.DelayMs);

            int linesBefore = lines.Count;
            CollectElementText(element, collected, lines);
            bool gotNewText = lines.Count > linesBefore;

            if (gotNewText)
            {
                var newLines = lines.GetRange(linesBefore, lines.Count - linesBefore);
                scrap?.WriteLines(newLines);
                lastNewTextTick = Environment.TickCount64;
                AppLogger.Log($"[Enhanced UIA] DeepCollect #{attempt}: +{newLines.Count}줄 (총 {lines.Count}줄)");

                // 날짜 범위 필터: 시작일 이전 날짜 감지 → 스크롤 중지
                if (scroll.FilterStartDate.HasValue && DateMatchHelper.ShouldStopScrolling(newLines, scroll.FilterStartDate.Value))
                {
                    AppLogger.Log($"[Filter] 시작일({scroll.FilterStartDate.Value:yyyy-MM-dd}) 이전 날짜 감지 → DeepCollect 스크롤 중지");
                    break;
                }

                if (attempt % 5 == 0)
                    progress?.Report($"Enhanced UIA 스크롤 중... ({attempt}, {lines.Count}줄)");
                continue;
            }

            long elapsed = Environment.TickCount64 - lastNewTextTick;
            if (elapsed < noNewTextStopMs)
                continue;

            AppLogger.Log($"[Enhanced UIA] DeepCollect #{attempt}: {elapsed / 1000}초간 새 텍스트 없음 → 자동 종료");
            progress?.Report($"스크롤 자동 종료 — {elapsed / 1000}초간 새 텍스트 없음");
            break;
        }

        AppLogger.Log($"[Enhanced UIA] DeepCollect 완료: {lines.Count}줄, {string.Join(Environment.NewLine, lines).Length}자");
        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : null;
    }

    private static void CollectElementText(
        AutomationElement element, HashSet<string> collected, List<string> lines)
    {
        try
        {
            var elements = element.FindAll(TreeScope.Descendants, Condition.TrueCondition);

            foreach (AutomationElement elem in elements)
            {
                try
                {
                    string name = elem.Current.Name ?? "";
                    if (!string.IsNullOrWhiteSpace(name) && collected.Add(name))
                        lines.Add(name);

                    if (elem.TryGetCurrentPattern(ValuePattern.Pattern, out object? vp))
                    {
                        string val = ((ValuePattern)vp).Current.Value ?? "";
                        if (!string.IsNullOrWhiteSpace(val) && collected.Add(val))
                            lines.Add(val);
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[Enhanced UIA] CollectElementText 예외", ex);
        }
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
