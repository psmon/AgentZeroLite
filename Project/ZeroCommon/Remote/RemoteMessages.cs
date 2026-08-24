// ─────────────────────────────────────────────────────────────
// Remote feature — actor message protocol
//
// Lives in ZeroCommon (feature-local, like Voice/Streams/VoiceStreamMessages.cs)
// so the hub/session actors stay headlessly testable. The WPF-side
// RemoteServerHost owns the HttpListener + WebSocket I/O and talks to these
// actors purely through the messages below.
//
//   /user/stage/remote                     RemoteHubActor      (connection registry + cap)
//   /user/stage/remote/session-{key}       RemoteSessionActor  (one per web session key)
// ─────────────────────────────────────────────────────────────

using Akka.Actor;
using Agent.Common.Services;

namespace Agent.Common.Remote;

// ── Stage gateway (host → StageActor → RemoteHubActor) ────────

/// <summary>Ask StageActor to instantiate the singleton hub under <c>/user/stage/remote</c>.
/// Reply: <see cref="RemoteHubCreated"/>. Idempotent.</summary>
public sealed record CreateRemoteHub(int MaxConnections);

/// <summary>Reply for <see cref="CreateRemoteHub"/>.</summary>
public sealed record RemoteHubCreated(IActorRef Hub);

// ── Session lifecycle (host → RemoteHubActor) ────────────────

/// <summary>
/// Ask the hub to create a per-web-session intermediary bound to <paramref name="Session"/>.
/// The hub enforces the connection cap and replies with either
/// <see cref="RemoteSessionCreated"/> or <see cref="RemoteSessionRejected"/>.
///
/// <para><see cref="SendToWeb"/> is invoked (from the session actor's Receive thread) with a
/// ready-to-send WS envelope string; the host implementation enqueues it onto the connection's
/// outbound channel. The actor holds the <see cref="ITerminalSession"/> and subscribes to its
/// output — it is the sole intermediary between the terminal and the socket.</para>
/// </summary>
public sealed record CreateRemoteSession(string SessionKey, ITerminalSession Session, Action<string> SendToWeb);

/// <summary>Reply: the session actor was created.</summary>
public sealed record RemoteSessionCreated(IActorRef SessionRef);

/// <summary>Reply: creation refused (e.g. connection cap reached).</summary>
public sealed record RemoteSessionRejected(string Reason);

/// <summary>Tell the hub to stop the session with this key (web socket closed).</summary>
public sealed record CloseRemoteSession(string SessionKey);

/// <summary>Query the hub. Reply: <see cref="RemoteStatusReply"/>.</summary>
public sealed record QueryRemoteStatus;

/// <summary>Hub status: active connections, the cap, and the active session keys.</summary>
public sealed record RemoteStatusReply(int Active, int Max, IReadOnlyList<string> Keys);

/// <summary>Update the connection cap at runtime (settings changed).</summary>
public sealed record SetMaxConnections(int Max);

// ── Inbound from web (host → RemoteSessionActor) ─────────────

/// <summary>Raw keystrokes from the web terminal — written to the PTY verbatim
/// (xterm already emits <c>\r</c>, control chars, etc.).</summary>
public sealed record RemoteInputText(string Text);

/// <summary>A named control key from the web client (e.g. "ctrlc", "esc", "up") —
/// mapped to <see cref="TerminalControl"/> and sent via <c>SendControl</c>.</summary>
public sealed record RemoteControlKey(string Key);
