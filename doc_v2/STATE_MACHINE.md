# State Machine

## Overview

The project no longer uses a monolithic enum that mixes call lifecycle, media mode
and booth line behavior together.

Current design:

- booth line state is handled by the booth flow and booth snapshot model
- WebRTC lifecycle is handled by `ConnectionStateMachine`
- media, route and signaling mode are handled by `ConnectionSession`
- UI reads booth and connection snapshots instead of poking coordinators directly
- FCM wakeup is not a state source; it only helps the app re-enter the booth flow

This keeps the FSM small and avoids combinatorial state growth.

## Booth Line State

`BoothLineState`

- `Idle`
- `Dialing`
- `RingingOutgoing`
- `RingingIncoming`
- `Connecting`
- `InCall`

Typical booth transitions:

- `Idle -> Dialing -> RingingOutgoing -> Connecting -> InCall`
- `Idle -> RingingIncoming -> Connecting -> InCall`
- `RingingIncoming -> Idle` on reject or timeout
- `RingingOutgoing -> Idle` on reject, offline, busy or timeout
- `InCall -> Idle` on hangup or terminal cleanup

Important rules:

- after any booth socket reconnect, the client must trust the server `line_snapshot`
  over stale local assumptions about ringing or connecting state
- after FCM wakeup, the client must still reconnect booth socket and trust the next
  `line_snapshot`; push payload itself is not authoritative state

## Connection Lifecycle States

`ConnectionLifecycleState`

- `Idle`
- `Preparing`
- `Signaling`
- `Connecting`
- `Connected`
- `Failed`
- `Closed`

## Allowed Shape of WebRTC Transitions

Typical connect path:

- `Idle -> Preparing`
- `Preparing -> Signaling`
- `Signaling -> Connecting`
- `Connecting -> Connected`

Terminal paths:

- `Preparing -> Failed`
- `Signaling -> Failed`
- `Connecting -> Failed`
- `Connected -> Closed`
- `Connected -> Failed`

Cancellation path:

- active state -> `Closed`

## State Axes Outside the WebRTC FSM

### MediaMode

- `AudioOnly`
- `DataOnly`
- `Full`

Current meaning:

- `AudioOnly`: normal voice-call baseline
- `DataOnly`: degraded mode after quality-policy decision
- `Full`: current audio+video session mode

### RouteMode

- `Direct`
- `Relay`
- `ManualBootstrap`

Current runtime mostly uses:

- `Direct`
- `Relay` when fallback or forced relay path wins

### SignalingMode

- `WorkerPolling`
- `WorkerWebSocket`
- `Manual`

Current runtime semantics:

- booth control events use `WorkerWebSocket`
- SDP and hangup slots still use `WorkerPolling`
- `Manual` remains legacy/debug-only

## Snapshot Models

### BoothSnapshot

`BoothSnapshot` is the read model for the dial screen.

It contains:

- booth number
- booth line state
- peer booth number when relevant
- active or pending call reference when relevant
- optional message for user-facing status

### ConnectionSnapshot

`ConnectionSnapshot` is the user-facing WebRTC read model.

It contains:

- lifecycle state
- media mode
- route mode
- signaling mode
- session identity
- caller/callee role information
- data-channel readiness
- reconnect attempt count
- last error

UI should render from snapshots instead of reaching into coordinators.

## User-Facing Semantics

Current intended mapping:

- booth `Idle` -> ready to dial or receive a call
- booth `RingingIncoming` -> user should accept or reject
- booth `RingingOutgoing` -> waiting for remote answer
- booth `Connecting` + connection `Preparing/Signaling/Connecting` -> call setup in progress
- booth `InCall` + connection `Connected` -> active call
- connection `Failed` -> session lost
- connection `Closed` -> session ended intentionally or by remote hangup

## Disconnect Handling

Current baseline behavior is intentionally simple:

- poor quality alone does not mean the lifecycle leaves `Connected`
- `Disconnected` starts a short grace period
- if ICE comes back during that grace period, the session stays alive
- if not, the call transitions to `Failed`
- booth line state should return to `Idle` only when booth control flow confirms terminal teardown

## Why This Model Was Chosen

The old style of states such as:

- `ConnectedAudio`
- `ConnectedDataOnly`
- `RelayFallback`
- `ManualHandshakePending`
- `WaitingForPeer`

creates unnecessary state explosion and mixes booth UX with WebRTC internals.

The current model is smaller, easier to log, and easier to extend.
