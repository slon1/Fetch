# Architecture

## Product Direction

The app is a Unity-based 1:1 P2P messenger designed for hostile or degraded
networks.

The current product model is a telephone switchboard model:

- every install owns one stable booth number
- the user can see their own number and manually dial another one
- booth availability is determined by a live Durable Object WebSocket
- call setup is manual: dial, ring, accept or reject
- WebRTC transport logic stays adaptive and independent from booth UX

## Layering

### Presentation

Presentation classes are Unity `MonoBehaviour` views and do not talk to
transport code directly.

Current presentation layer:

- `LobbyScreenView` acting as the booth dial screen shell
- `CallScreenView`
- `ConnectionStatusView`

Responsibilities:

- bind scene references
- expose UI events
- render booth, call and status state
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
- show a standalone runtime fatal overlay independent of the normal booth/call UI

Current limitation:

- native plugin crashes or hard IL2CPP aborts are not guaranteed to be caught

## Application Layer

### Booth

`BoothFlowCoordinator`

Responsibilities:

- register or confirm local booth ownership
- keep the local booth socket connected
- publish booth line snapshots to UI
- dial another booth number
- accept or reject incoming calls
- mark local line as `in_call`
- hang up and reset booth line state

Booth model invariants:

- one booth number per install in v1
- one active socket per booth in v1
- booth ownership is persistent
- booth online/offline is live-presence only and comes from the socket
- booth reconnect must trust `line_snapshot` from the server as source of truth

### Connection

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

This coordinator remains the main WebRTC orchestrator and should stay readable.
The booth migration should not push booth product-state concerns into ICE/media code.

## Runtime State Model

The runtime model separates booth state from media/session state.

### Booth Line State

`BoothLineState`

- `Idle`
- `Dialing`
- `RingingOutgoing`
- `RingingIncoming`
- `Connecting`
- `InCall`

### Connection Lifecycle

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

- booth registration
- dial / accept / reject / hangup / connected commands
- signaling slot API calls
- polling behavior for missing signaling messages

It does not own business decisions.

### BoothSocketService

`BoothSocketService` owns the local control WebSocket to the user's own booth.

Responsibilities:

- connect and reconnect booth socket
- dispatch booth control events to the application layer
- keep socket lifecycle separate from booth business logic

Socket events currently cover:

- `line_snapshot`
- `incoming_call`
- `outgoing_ringing`
- `call_accepted`
- `call_rejected`
- `remote_hangup`
- `line_reset`

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

## Server Domain Model

### BoothDurableObject

One booth DO per booth number.

Responsibilities:

- hold booth registration record
- hold booth line state
- terminate superseded booth sockets
- emit `line_snapshot` and booth control events
- answer whether a booth is registered and online

Persistent booth record is intentionally minimal:

- `boothNumber`
- `ownerClientId`
- `createdAt`
- `lastSeenAt`

Presence is not persistent; it is derived from the live socket.

### LobbyDurableObject

The class name remains for migration convenience, but functionally it now acts as the
switchboard coordinator.

Responsibilities:

- validate dial attempts
- resolve `not_registered`, `offline`, `busy` and `ringing`
- create call bindings
- coordinate accept / reject / connected / hangup transitions
- reset both booths on terminal call actions

### RoomDurableObject

The class name remains for migration convenience, but functionally it now acts as the
call-session store.

Responsibilities:

- store one call record per `callId`
- hold signaling slots (`offer`, `answer`, `hangup`)
- enforce ring timeout and terminal cleanup retention
- provide durable storage independent of booth socket reconnects

## Control Plane Split

The runtime control plane is intentionally hybrid.

Through booth socket events:

- incoming call wakeup
- outgoing ringing
- call accepted
- call rejected
- remote hangup
- line reset
- line snapshot after reconnect

Through HTTP endpoints:

- booth registration
- dial / accept / reject / hangup / connected commands
- `offer`
- `answer`
- `hangup`
- ICE restart signaling reuse through the same slot mechanism

Through `RTCDataChannel`:

- chat
- lightweight live UX commands such as peer-speaking state

This split keeps low-latency booth control on WebSocket while keeping SDP and
other retriable payloads on HTTP.

## Call Role Rules

- caller is always offerer
- callee is always answerer
- booth/socket reconnect does not redefine call roles
- if both users dial each other nearly simultaneously, the switchboard should avoid a user-facing failure and converge onto one pending call

## ICE Server Strategy

Current ICE configuration is built from two sources:

- STUN URLs from `AppConfig`
- TURN URLs and credentials from `StreamingAssets/dev-secrets.json`

Transport assumptions:

- direct path first
- relay fallback on the next attempt after selected failure paths
- multiple STUN/TURN endpoints supported
- relay path can carry a stricter media policy later, including video disable

The booth migration must not simplify away transport resilience.
That transport stack is one of the main assets of the project.
