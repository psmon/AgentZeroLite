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
        AppLogger.Log($"[Chromium] TryCaptureAsync start | hwnd=0x{hwnd:X8}, title=\"{windowTitle}\"");

        progress?.Report("Chromium detected — trying Enhanced UIA...");
        AppLogger.Log("[Enhanced UIA] start");
        try
        {
            var s = scroll ?? new ScrollOptions();
            string? uiaResult = await Task.Run(() => TryEnhancedUiAutomation(hwnd, ct, progress, scrap, askContinue, s, pickPoint), ct);
            if (!string.IsNullOrWhiteSpace(uiaResult))
            {
                AppLogger.Log($"[Enhanced UIA] captured | {uiaResult.Length} chars");
                progress?.Report("Captured with Enhanced UIA");
                return uiaResult;
            }
            AppLogger.Log("[Enhanced UIA] no result");
        }
        catch (OperationCanceledException)
        {
            AppLogger.Log("[Enhanced UIA] cancelled");
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[Enhanced UIA] threw", ex);
        }

        AppLogger.Log("[Chromium] Enhanced UIA failed — falling through to the older strategy");
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
        AppLogger.Log($"[Enhanced UIA] start | hwnd=0x{hwnd:X8}");

        var root = AutomationElement.FromHandle(hwnd);

        progress?.Report("Enhanced UIA: looking for content elements...");
        AutomationElement? document = null;

        foreach (var controlType in ContentControlTypes)
        {
            AppLogger.Log($"[Enhanced UIA] FindFirst(ControlType.{controlType.ProgrammaticName}) ...");
            var condition = new PropertyCondition(
                AutomationElement.ControlTypeProperty, controlType);
            document = root.FindFirst(TreeScope.Descendants, condition);

            if (document is not null)
            {
                AppLogger.Log($"[Enhanced UIA] {controlType.ProgrammaticName} found!");
                break;
            }
        }

        if (document is null)
        {
            progress?.Report("Enhanced UIA: forcing the accessibility tree to build...");
            AppLogger.Log("[Enhanced UIA] forcing the accessibility tree with FindAll(TrueCondition)...");
            var allElements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            AppLogger.Log($"[Enhanced UIA] FindAll: {allElements.Count} elements");
            Thread.Sleep(500);
            ct.ThrowIfCancellationRequested();

            foreach (var controlType in ContentControlTypes)
            {
                var condition = new PropertyCondition(
                    AutomationElement.ControlTypeProperty, controlType);
                document = root.FindFirst(TreeScope.Descendants, condition);
                if (document is not null)
                {
                    AppLogger.Log($"[Enhanced UIA] retry: {controlType.ProgrammaticName} found!");
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
            AppLogger.Log($"[Enhanced UIA] {allDocs.Count} content elements found");
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
            AppLogger.Log("[Enhanced UIA] no content element found — giving up");
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
        AppLogger.Log($"[Enhanced UIA] {allContentElements.Count} content elements");

        string? bestResult = null;

        foreach (var contentElem in allContentElements)
        {
            if (ct.IsCancellationRequested)
            {
                AppLogger.Log($"[Enhanced UIA] cancelled — returning a partial result ({bestResult?.Length ?? 0} chars)");
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
                AppLogger.Log("[Enhanced UIA] ⚠ the VS Code editor is not reachable!");
                AppLogger.Log("[Enhanced UIA] → press Shift+Alt+F1 in VS Code, or set editor.accessibilitySupport: on");
                progress?.Report("⚠ VS Code needs screen-reader mode (Shift+Alt+F1)");
                continue;
            }

            AppLogger.Log($"[Enhanced UIA] {elemType}: \"{Truncate(elemName, 60)}\"");
            progress?.Report($"Enhanced UIA: {elemType} — extracting text...");

            string? textPatternResult = TryTextPatternOnElement(contentElem);
            if (!string.IsNullOrWhiteSpace(textPatternResult))
            {
                AppLogger.Log($"[Enhanced UIA] TextPattern | {textPatternResult.Length} chars");
                if (textPatternResult.Length > (bestResult?.Length ?? 0))
                {
                    scrap?.WriteAll(textPatternResult);
                    bestResult = textPatternResult;
                }
            }

            string? deepResult = TryDeepCollect(contentElem, ct, progress, scrap, askContinue, scroll, pickPoint);
            if (!string.IsNullOrWhiteSpace(deepResult))
            {
                AppLogger.Log($"[Enhanced UIA] DeepCollect | {deepResult.Length} chars");
                if (deepResult.Length > (bestResult?.Length ?? 0))
                    bestResult = deepResult;
            }
        }

        if (!string.IsNullOrWhiteSpace(bestResult))
        {
            bool cancelled = ct.IsCancellationRequested;
            AppLogger.Log($"[Enhanced UIA] final: {bestResult.Length} chars{(cancelled ? " (partial, cancelled)" : "")}");
            return bestResult;
        }

        AppLogger.Log("[Enhanced UIA] could not extract text from any content element");
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
            AppLogger.LogError("[Enhanced UIA] TextPattern threw", ex);
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
            AppLogger.Log("[Enhanced UIA] DeepCollect: no BoundingRectangle, cannot scroll");
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
                    // 역방향이면 최하단(100%)에서 시작해 위로 올라가며 수집.
                    scrollPattern.SetScrollPercent(ScrollPattern.NoScroll, scroll.IsReverse ? 100 : 0);
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
            AppLogger.Log($"[Enhanced UIA] DeepCollect scroll point: ({scrollX},{scrollY})");
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
        AppLogger.Log($"[Enhanced UIA] DeepCollect first pass: {lines.Count} lines");

        int maxScrollAttempts = scroll.MaxAttempts;
        const long noNewTextStopMs = 20_000;

        long lastNewTextTick = Environment.TickCount64;

        for (int attempt = 0; attempt < maxScrollAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
            {
                AppLogger.Log($"[Enhanced UIA] DeepCollect: cancelled — returning {lines.Count} lines");
                break;
            }

            if (NativeMethods.GetForegroundWindow() != fgWindow)
            {
                AppLogger.Log("[Enhanced UIA] DeepCollect: lost focus — stopping the scroll");
                break;
            }

            // 역방향은 휠을 위로(+), 정방향은 아래로(-).
            int wheelDelta = NativeMethods.WHEEL_DELTA * scroll.DeltaMultiplier;
            NativeMethods.mouse_event(
                NativeMethods.MOUSEEVENTF_WHEEL, 0, 0,
                scroll.IsReverse ? wheelDelta : -wheelDelta, IntPtr.Zero);
            Thread.Sleep(scroll.DelayMs);

            int linesBefore = lines.Count;
            CollectElementText(element, collected, lines);
            bool gotNewText = lines.Count > linesBefore;

            if (gotNewText)
            {
                var newLines = lines.GetRange(linesBefore, lines.Count - linesBefore);
                scrap?.WriteLines(newLines);
                lastNewTextTick = Environment.TickCount64;
                AppLogger.Log($"[Enhanced UIA] DeepCollect #{attempt}: +{newLines.Count} lines ({lines.Count} total)");

                // 날짜 범위 필터: 시작일 이전 날짜 감지 → 스크롤 중지
                if (scroll.FilterStartDate.HasValue && DateMatchHelper.ShouldStopScrolling(newLines, scroll.FilterStartDate.Value))
                {
                    AppLogger.Log($"[Filter] start date ({scroll.FilterStartDate.Value:yyyy-MM-dd}) reached an older date — stopping the DeepCollect scroll");
                    break;
                }

                if (attempt % 5 == 0)
                    progress?.Report($"Enhanced UIA scrolling... ({attempt}, {lines.Count} lines)");
                continue;
            }

            long elapsed = Environment.TickCount64 - lastNewTextTick;
            if (elapsed < noNewTextStopMs)
                continue;

            AppLogger.Log($"[Enhanced UIA] DeepCollect #{attempt}: no new text for {elapsed / 1000}s — stopping");
            progress?.Report($"Scroll stopped — no new text for {elapsed / 1000}s");
            break;
        }

        AppLogger.Log($"[Enhanced UIA] DeepCollect done: {lines.Count} lines, {string.Join(Environment.NewLine, lines).Length} chars");
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
            AppLogger.LogError("[Enhanced UIA] CollectElementText threw", ex);
        }
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
