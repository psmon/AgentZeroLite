using System.IO;
using System.Text;

namespace AgentZeroWpf;

/// <summary>
/// CLI 행동 로그 — mouseclick, keypress 등 수행 이력을 기록하고 조회.
/// AI가 이전 행동과 반응을 파악하는 데 활용.
/// </summary>
internal static class CliActionLog
{
    private static readonly string LogDir = Path.Combine(AppContext.BaseDirectory, "logs");
    private static readonly string LogFile = Path.Combine(LogDir, "cli-actions.log");
    private const int MaxLines = 500;

    /// <summary>행동 로그 한 줄 추가.</summary>
    public static void Log(string action, string? detail = null)
    {
        Directory.CreateDirectory(LogDir);
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string entry = detail != null
            ? $"[{timestamp}] {action} | {detail}"
            : $"[{timestamp}] {action}";

        File.AppendAllText(LogFile, entry + Environment.NewLine, Encoding.UTF8);
    }

    /// <summary>클릭 후 반응 감지 — 포그라운드 윈도우 타이틀 변경 여부.</summary>
    public static string DetectClickReaction(int x, int y, string? titleBefore)
    {
        // 클릭 후 짧은 대기
        Thread.Sleep(300);

        IntPtr fg = NativeMethods.GetForegroundWindow();
        string titleAfter = GetWindowTitle(fg);

        var sb = new StringBuilder();
        sb.Append($"click ({x},{y})");

        if (titleBefore != null && titleAfter != titleBefore)
            sb.Append($" → title changed: \"{Truncate(titleAfter, 80)}\"");
        else
            sb.Append($" → title: \"{Truncate(titleAfter, 80)}\"");

        // 클릭 위치의 윈도우 정보
        var pt = new NativeMethods.POINT { X = x, Y = y };
        IntPtr hwndAt = NativeMethods.WindowFromPoint(pt);
        if (hwndAt != IntPtr.Zero)
        {
            IntPtr root = NativeMethods.GetAncestor(hwndAt, NativeMethods.GA_ROOT);
            string rootTitle = GetWindowTitle(root);
            if (rootTitle != titleAfter)
                sb.Append($" | window at point: \"{Truncate(rootTitle, 60)}\"");
        }

        return sb.ToString();
    }

    /// <summary>최근 N개 로그 반환.</summary>
    public static string[] GetRecent(int count = 50)
    {
        if (!File.Exists(LogFile))
            return [];

        var lines = File.ReadAllLines(LogFile, Encoding.UTF8);
        int skip = Math.Max(0, lines.Length - count);
        return lines.Skip(skip).ToArray();
    }

    /// <summary>로그 전체 줄 수.</summary>
    public static int GetTotalCount()
    {
        if (!File.Exists(LogFile))
            return 0;
        return File.ReadAllLines(LogFile, Encoding.UTF8).Length;
    }

    /// <summary>오래된 로그 정리 (MaxLines 초과 시).</summary>
    public static void Trim()
    {
        if (!File.Exists(LogFile)) return;
        var lines = File.ReadAllLines(LogFile, Encoding.UTF8);
        if (lines.Length > MaxLines)
        {
            var keep = lines.Skip(lines.Length - MaxLines).ToArray();
            File.WriteAllLines(LogFile, keep, Encoding.UTF8);
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int len = NativeMethods.GetWindowTextLength(hwnd);
        if (len == 0) return "";
        var buf = new char[len + 1];
        NativeMethods.GetWindowText(hwnd, buf, buf.Length);
        return new string(buf, 0, len);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
