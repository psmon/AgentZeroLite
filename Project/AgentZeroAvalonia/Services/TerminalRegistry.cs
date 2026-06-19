using System.Collections.Generic;
using System.Linq;
using Agent.Common.Services;

namespace AgentZeroAvalonia.Services;

/// <summary>
/// 살아있는 터미널 세션을 (group, tab) 좌표로 보관하는 전역 레지스트리.
///
/// WPF는 MainWindow의 group/tab UI 모델이 <see cref="ITerminalSession"/> ref를
/// 직접 들고 있어 toolbelt가 거기서 세션을 해소했다. Avalonia 포트에서는 그
/// 역할을 이 레지스트리가 맡는다 — 터미널 탭이 세션을 등록하고, 에이전트
/// toolbelt(<see cref="AgentZeroAvalonia.Tools.PtyTerminalToolbelt"/>)가 조회한다.
///
/// 정적 싱글톤 — ActorSystemManager와 같은 앱 전역 접근 패턴을 따른다.
/// </summary>
public static class TerminalRegistry
{
    public readonly record struct Entry(int Group, int Tab, string Title, ITerminalSession Session);

    private static readonly object Sync = new();
    private static readonly List<Entry> Entries = new();

    public static void Register(int group, int tab, string title, ITerminalSession session)
    {
        lock (Sync)
        {
            Entries.RemoveAll(e => e.Group == group && e.Tab == tab);
            Entries.Add(new Entry(group, tab, title, session));
        }
    }

    public static void Unregister(ITerminalSession session)
    {
        lock (Sync) Entries.RemoveAll(e => ReferenceEquals(e.Session, session));
    }

    public static bool TryResolve(int group, int tab, out ITerminalSession? session)
    {
        lock (Sync)
        {
            foreach (var e in Entries)
            {
                if (e.Group == group && e.Tab == tab) { session = e.Session; return true; }
            }
            session = null;
            return false;
        }
    }

    /// <summary>리스트 표시용 스냅샷 (group, tab, title, running).</summary>
    public static IReadOnlyList<(int Group, int Tab, string Title, bool Running)> Snapshot()
    {
        lock (Sync)
            return Entries
                .Select(e => (e.Group, e.Tab, e.Title, e.Session.IsRunning))
                .ToList();
    }
}
