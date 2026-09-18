using Akka.Actor;
using Agent.Common;
using Agent.Common.Actors;
using AgentZeroAvalonia.Actors;
using AgentZeroAvalonia.ViewModels;

namespace AgentZeroAvalonia.Services;

/// <summary>
/// The workspace/terminal side of the actor topology (M0035): the same messages the WPF
/// host's <c>BindSessionToActors</c> sends, minus <c>UpdateTerminalHwnd</c> — there is no
/// HWND to report for a renderer that is not a Win32 window on every OS.
/// </summary>
internal static class TerminalActorBinder
{
    public static void RegisterWorkspace(WorkspaceViewModel ws)
    {
        if (!ActorSystemManager.IsInitialized) return;
        ActorSystemManager.Stage.Tell(new RegisterWorkspace(ws.DisplayName, ws.DirectoryPath), ActorRefs.NoSender);
        AppLogger.Log($"[Akka] Workspace registered: {ws.DisplayName} ({ws.DirectoryPath})");
    }

    public static void UnregisterWorkspace(WorkspaceViewModel ws)
    {
        if (!ActorSystemManager.IsInitialized) return;
        ActorSystemManager.Stage.Tell(new UnregisterWorkspace(ws.DisplayName), ActorRefs.NoSender);
    }

    /// <summary>Create the terminal actor for a freshly started session and bind it. Idempotent per session id.</summary>
    public static void Bind(WorkspaceViewModel ws, TerminalTabViewModel tab)
    {
        if (!ActorSystemManager.IsInitialized || tab.Session is null) return;
        if (tab.LastBoundSessionId == tab.Session.SessionId) return;

        ActorSystemManager.Stage.Tell(new CreateTerminalInWorkspace(ws.DisplayName, tab.Title, tab.Session.SessionId), ActorRefs.NoSender);
        ActorSystemManager.Stage.Tell(new BindSessionInWorkspace(ws.DisplayName, tab.Title, tab.Session), ActorRefs.NoSender);
        tab.LastBoundSessionId = tab.Session.SessionId;
        AppLogger.Log($"[Akka] Terminal actor bound: {ws.DisplayName}/{tab.Title} session={tab.Session.SessionId}");
    }

    public static void Destroy(WorkspaceViewModel ws, TerminalTabViewModel tab)
    {
        if (!ActorSystemManager.IsInitialized) return;
        if (tab.LastBoundSessionId is null) return;
        ActorSystemManager.Stage.Tell(new DestroyTerminalInWorkspace(ws.DisplayName, tab.Title), ActorRefs.NoSender);
        tab.LastBoundSessionId = null;
    }

    /// <summary>The terminal actor is named by the tab title; renaming the tab renames the actor.</summary>
    public static void Rename(WorkspaceViewModel ws, TerminalTabViewModel tab, string newTitle)
    {
        if (!ActorSystemManager.IsInitialized || tab.LastBoundSessionId is null) return;
        ActorSystemManager.Stage.Tell(new RenameTerminalInWorkspace(ws.DisplayName, tab.Title, newTitle), ActorRefs.NoSender);
    }

    public static void SetActive(WorkspaceViewModel? ws, TerminalTabViewModel? tab)
    {
        if (!ActorSystemManager.IsInitialized) return;
        ActorSystemManager.Stage.Tell(new SetActiveTerminal(ws?.DisplayName, tab?.Title), ActorRefs.NoSender);
    }
}
