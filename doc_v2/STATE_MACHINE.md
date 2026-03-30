# State Machine

## Overview

The project no longer uses a monolithic state enum that encodes both lifecycle
and media mode together.

Current design:

- lifecycle is handled by `ConnectionStateMachine`
- media, route, and signaling mode are handled by `ConnectionSession`
- UI reads `ConnectionSnapshot`

This keeps the FSM small and avoids combinatorial state growth.

## Lifecycle States

`ConnectionLifecycleState`

- `Idle`
- `Preparing`
- `Signaling`
- `Connecting`
- `Connected`
- `Recovering`
- `Failed`
- `Closed`

## Allowed Shape of Transitions

Typical connect path:

- `Idle -> Preparing`
- `Preparing -> Signaling`
- `Signaling -> Connecting`
- `Connecting -> Connected`

Recovery path:

- `Connected -> Recovering`
- `Recovering -> Connected`
- `Recovering -> Failed`

Terminal paths:

- `Preparing -> Failed`
- `Signaling -> Failed`
- `Connecting -> Failed`
- `Connected -> Closed`
- `Connected -> Failed`
- `Recovering -> Closed`
- `Recovering -> Failed`

Cancellation path:

- active state -> `Closed`

## State Axes Outside the FSM

### MediaMode

- `AudioOnly`
- `DataOnly`
- `Full` reserved

Current meaning:

- `AudioOnly`: normal voice call baseline
- `DataOnly`: degraded mode after quality-policy decision

### RouteMode

- `Direct`
- `Relay`
- `ManualBootstrap`

Current runtime mostly uses:

- `Direct`

### SignalingMode

- `WorkerPolling`
- `WorkerWebSocket`
- `Manual`

Current runtime uses:

- `WorkerPolling`

## Snapshot Model

`ConnectionSnapshot` is the user-facing read model.

It contains:

- lifecycle state
- media mode
- route mode
- signaling mode
- session identity
- creator role
- data-channel readiness
- reconnect attempt count
- last error

UI should read the snapshot instead of reaching into coordinators.

## Compatibility Layer

There is still a temporary compatibility mapping from `ConnectionSnapshot` back
to the older `ConnectionState` enum for code that has not fully migrated.

This compatibility layer should eventually be removed.

## User-Facing Semantics

Current intended mapping:

- `Connected + AudioOnly` -> normal active call
- `Connected + DataOnly` -> limited connection
- `Recovering` -> trying to restore session
- `Failed` -> session lost
- `Closed` -> session ended intentionally or by cleanup

## Recovery Semantics

Recovery is intentionally separate from degradation.

- poor quality alone does not mean the lifecycle leaves `Connected`
- `Disconnected` may start grace handling and then move to `Recovering`
- successful ICE restart returns to `Connected`
- failed recovery budget ends in `Failed`

## Why This Model Was Chosen

The old style of states such as:

- `ConnectedAudio`
- `ConnectedDataOnly`
- `RelayFallback`
- `ManualHandshakePending`

creates unnecessary state explosion.

The current model is smaller, easier to log, and easier to extend.
