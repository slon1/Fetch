# Architecture

## Product Direction

The app is a Unity-based 1:1 P2P messenger designed for hostile or degraded
networks.

The current architecture aims to keep:

- UI thin
- orchestration explicit
- transport details inside adapters and clients
- recovery policy separate from low-level sampling
- failure states diagnosable on real user devices

## Layering

### Presentation

Presentation classes are Unity `MonoBehaviour` views and do not talk to
transport code directly.

Current presentation layer:

- `LobbyScreenView`
- `CallScreenView`
- `ConnectionStatusView`
- `RoomListItemView`

Responsibilities:

- bind scene references
- expose UI events
- render lobby, call and status state
- stay ignorant of Worker and WebRTC internals

### Bootstrap

`AppBootstrap` is the composition root for `game.unity`.

Responsibilities:

- run startup preflight checks
- register global managed exception hooks
- create the service graph
- wire application services to views
- own app lifetime
- switch into fatal-error mode when startup/runtime reaches a managed terminal fault

`AppBootstrap` must not own offer/answer logic or raw HTTP details.

### Crash Handling

Crash handling is a separate local-only concern.

Current pieces:

- `CrashCoordinator`
- `CrashReport`
- `ICrashReportSink`
- `LocalCrashReportSink`
- `StartupPreflightValidator`
- `FatalErrorView`

Responsibilities:

- collect recent Unity log tail
- capture managed startup/runtime failures
- persist latest local crash report
- show a standalone runtime fatal overlay independent of the normal lobby/call UI

Current limitation:

- native plugin crashes or hard IL2CPP aborts are not guaranteed to be caught

### Application Layer

The application layer is split by use case.

#### Room

`RoomFlowCoordinator`

Responsibilities:

- bootstrap lobby on startup
- list rooms
- create own waiting room
- join foreign room
- poll owned room state until peer join
- delete stale or terminal rooms

`RoomHeartbeatService`

Responsibilities:

- keep waiting-room ownership alive with periodic heartbeat
- stop/start with lobby lifecycle
- stay independent from WebRTC degradation logic

#### Connection

`ConnectionFlowCoordinator`

Responsibilities:

- caller and callee connect flow
- peer setup
- signaling orchestration
- hangup
- chat send/receive
- state publication
- quality sampling lifecycle
- degradation execution
- recovery execution

This coordinator is still the main orchestrator and should stay readable. If it
grows too much, future extraction should happen along responsibilities, not as
an abstraction explosion.

## Runtime State Model

The runtime model separates lifecycle from media, route and signaling axes.

### Lifecycle

`ConnectionLifecycleState`

- `Idle`
- `Preparing`
- `Signaling`
- `Connecting`
- `Connected`
- `Recovering`
- `Failed`
- `Closed`

### Session Axes

`ConnectionSession` is the mutable runtime source of truth.

`ConnectionSnapshot` is the immutable read model exposed to UI.

Session dimensions:

- `MediaMode`
  - `AudioOnly`
  - `DataOnly`
  - `Full` reserved for future video
- `RouteMode`
  - `Direct`
  - `Relay`
  - `ManualBootstrap`
- `SignalingMode`
  - `WorkerPolling`
  - `WorkerWebSocket`
  - `Manual`

Additional runtime fields:

- `SessionId`
- `IsCreator`
- `HasOpenDataChannel`
- `ReconnectAttempts`
- `LastError`

## Transport Layer

### WorkerClient

`WorkerClient` is a plain HTTP client for the Cloudflare Worker.

It owns:

- lobby and room API calls
- room heartbeat calls
- signaling slot API calls
- polling behavior for missing signaling messages

It does not own business decisions.

### WebRtcPeerAdapter

`WebRtcPeerAdapter` wraps `RTCPeerConnection`.

Responsibilities:

- initialize peer connection
- create offer/answer
- create ICE-restart offer
- wait for ICE gathering
- expose events for track, data channel and ICE state

### MediaCaptureService

`MediaCaptureService` owns local microphone and camera capture state.

Current behavior:

- microphone capture is reused across sessions
- push-to-talk controls whether outgoing voice is enabled
- camera/video path exists, but the product baseline is still audio + data first

## Signaling and Lobby Strategy

Current signaling strategy is intentionally simple.

- Cloudflare Worker + Durable Objects
- HTTP polling
- non-trickle ICE
- one signaling slot per `(sessionId, type)`
- room state and lobby membership coordinated by Durable Objects, not KV

Current room and signaling message types:

- room status: `waiting`, `joined`, `closed`
- signaling slots: `offer`, `answer`, `hangup`

ICE restart currently reuses the same `offer` / `answer` slots and does not add
new protocol message types yet.

Lobby behavior:

- app bootstraps lobby automatically on start
- own waiting room is hidden from own lobby list
- if no foreign rooms are available, client creates its own waiting room
- WebRTC starts only after an actual room join
- waiting-room heartbeat is used for presence/freshness
- joined rooms disappear from lobby immediately, but room state lives until call terminal state

## Control Plane Split

The runtime control plane is intentionally hybrid.

Through Durable Objects:

- `offer`
- `answer`
- `hangup`
- ICE restart signaling
- waiting-room state
- room heartbeat / presence

Through `RTCDataChannel`:

- chat
- lightweight live UX commands such as peer-speaking state

This split keeps room/session coordination in the Worker while keeping chat and
fast in-call commands off the signaling backend.

## ICE Server Strategy

Current ICE configuration is built from two sources:

- STUN URLs from `AppConfig`
- TURN credentials and TURN URLs from `StreamingAssets/dev-secrets.json`

Current Metered integration uses static dashboard credentials for development.

Runtime behavior:

- default mode uses `RTCIceTransportPolicy.All`
- `relayOnly` switches to `RTCIceTransportPolicy.Relay`
- diagnostics log the selected nominated route, for example:
  - `relay/relay udp`
  - `srflx/host`
- relay path currently does not force-disable video yet; that remains a future
  policy decision

## Quality and Degradation

### Sampling

`WebRtcStatsSampler` periodically polls `RTCPeerConnection.GetStats()`.

Current `QualitySnapshot` fields:

- RTT
- jitter
- packet loss
- available outgoing bitrate
- ICE state
- selected nominated ICE route summary

Some fields may be unavailable depending on Unity WebRTC reports and are
treated as nullable or absent.

### Policy

`ConnectionPolicy` evaluates `ConnectionSnapshot + QualitySnapshot`.

Current implemented decision:

- `DowngradeToDataOnly`

Current downgrade behavior is pragmatic MVP:

- logical mode changes to `DataOnly`
- audio send path is disabled
- no full renegotiated remove-track flow yet

## Recovery

`RecoveryCoordinator` owns retry budget, grace period and backoff.

Current recovery strategy:

1. `Disconnected` starts a short grace period.
2. If the connection does not recover, lifecycle moves to `Recovering`.
3. A bounded ICE restart attempt is made.
4. If recovery succeeds, lifecycle returns to `Connected`.
5. If recovery budget is exhausted, lifecycle moves to `Failed`.
6. Terminal call states return the user to auto-lobby.

Current simplifications:

- caller initiates ICE restart
- callee answers restart offer
- same peer connection is reused
- no glare handling
- no relay escalation
- no peer recreation

## Background Notifications

Android best-effort local notifications are used while the Unity app is still
alive in background.

Current semantics:

- `PeerJoined` notification is auxiliary
- `Connected` notification is the main alert
- no remote push backend is involved
- if the app is killed and heartbeat dies, notifications stop entirely

## Legacy Boundary

The old manual signaling prototype remains only as a reference.

Current legacy boundary:

- `Assets/Scripts/ManualSignaling.cs`
- `Assets/Scenes/SampleScene.unity`

It should not drive new architectural decisions except as a transport sanity
reference.
