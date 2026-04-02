# Architecture

## Product Direction

The app is a Unity-based 1:1 P2P messenger designed for hostile or degraded
networks.

The current product model is a telephone switchboard model:

- every install owns one stable booth number
- the user can see their own number and manually dial another one
- booth availability is determined by a live Durable Object WebSocket in foreground
- Android background wakeup is handled through FCM push
- call setup is manual: dial, ring, accept or reject
- WebRTC transport logic stays adaptive and independent from booth UX

## Layering

### Presentation

Presentation classes are Unity `MonoBehaviour` views and do not talk to
transport code directly.

Current presentation layer:

- `CallerScr`
- `VideoScr`
- `ChatScr`
- `InfoScr`
- `AppUiCoordinator` as the single active UI facade

Responsibilities:

- bind scene references
- expose UI events
- render booth, call and status state
- stay ignorant of Worker and WebRTC internals

### Bootstrap

`AppBootstrap` is the composition root for `BT2.unity`.

Responsibilities:

- run startup preflight checks
- register global managed exception hooks
- create the service graph
- wire application services to views
- own app lifetime
- initialize FCM on Android
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
- booth online/offline is derived from live socket or stored FCM token reachability
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
- short disconnect-grace handling before terminal failure

This coordinator remains the main WebRTC orchestrator and should stay readable.
The booth model should not push booth product-state concerns into ICE/media code.

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
- `Failed`
- `Closed`

### Session Axes

`ConnectionSession` is the mutable runtime source of truth.

`ConnectionSnapshot` is the immutable read model exposed to UI.

Session dimensions:

- `MediaMode`
  - `AudioOnly`
  - `DataOnly`
  - `Full`
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
- booth push-token registration
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

Important limitation:

- booth socket is reliable for foreground/live control
- it is not a reliable background wakeup mechanism on mobile by itself
- FCM complements it for Android wakeup, but does not replace it as the foreground control plane

### FcmPushService

`FcmPushService` owns Android Firebase Messaging integration.

Responsibilities:

- initialize Firebase Messaging
- observe token lifecycle
- publish token updates back to bootstrap
- dispatch incoming-call push payloads to the app

Important rule:

- FCM is not the source of truth for line state
- after wakeup, the app must reconnect booth socket and trust server `line_snapshot`

### WebRtcPeerAdapter

`WebRtcPeerAdapter` wraps `RTCPeerConnection`.

Responsibilities:

- initialize peer connection
- create offer/answer
- wait for ICE gathering
- expose events for track, data channel and ICE state

### MediaCaptureService

`MediaCaptureService` owns local microphone and camera capture state.

Current behavior:

- microphone capture is reused across sessions
- push-to-talk controls whether outgoing voice is enabled
- camera/video path is active in the current booth baseline
- app background visibility can influence whether local video remains enabled

## Server Domain Model

### BoothDurableObject

One booth DO per booth number.

Responsibilities:

- hold booth registration record
- hold booth line state
- hold the latest FCM token for that booth in v1
- terminate superseded booth sockets
- emit `line_snapshot` and booth control events
- answer whether a booth is registered and reachable

Persistent booth record is intentionally minimal:

- `boothNumber`
- `ownerClientId`
- `createdAt`
- `lastSeenAt`
- `fcmToken`
- `fcmPlatform`
- `fcmUpdatedAt`

Presence is not persistent; it is derived from the live socket. FCM token storage is
used only for wakeup delivery, not as authoritative line state.

### LobbyDurableObject

The class name remains for migration convenience, but functionally it now acts as the
switchboard/orchestrator for booth dialing and call creation.

Responsibilities:

- validate dial requests
- decide `not_registered / offline / busy / ringing`
- coordinate booth state transitions for both peers
- trigger FCM send for Android background wakeup when a target booth has no socket but does have a valid token

### RoomDurableObject

The class name remains for migration convenience, but functionally it is the per-call
store.

Responsibilities:

- persist signaling slots per `callId`
- store `offer`, `answer`, `hangup`, `restart` payloads
- support best-effort cleanup after terminal call states

## Android Runtime Notes

- `MessagingUnityPlayerActivity` is the launcher activity to support Firebase Messaging background behavior
- the manifest declares `POST_NOTIFICATIONS`
- notification channel `booth-calls` is the default FCM notification channel
- local notification rendering is done through direct Android APIs, not the Unity mobile notifications package

## Current Active Assets

- scene: `Assets/Scenes/BT2.unity`
- worker: `Assets/Scripts/Server/worker.js`
- Android manifest: `Assets/Plugins/Android/AndroidManifest.xml`

Legacy client scenes and old lobby/call presentation classes were removed from this branch.
