# WebRTC v2 Docs

## Purpose

This folder documents the current Unity 1:1 messenger baseline.

The product target is a resilient communication app for degraded or partially
blocked networks, not a generic video-call demo.

Current priorities:

- keep a working 1:1 audio + data baseline
- survive short network disruptions
- keep lobby behavior deterministic
- keep failures diagnosable on real user devices

## Current Working Baseline

As of the current codebase, the following flow works in `game.unity`:

- auto-lobby bootstrap on app start
- list waiting rooms from Cloudflare Durable Objects
- hide own waiting room from own lobby list
- auto-create own waiting room when no foreign rooms are available
- join foreign waiting room
- start WebRTC only after room join
- audio call
- chat over `RTCDataChannel`
- hangup
- recovery through ICE restart after short disruptions
- fatal startup/runtime error screen for managed failures

Important caveat:

- the media/signaling baseline is considered working
- the current semi-automatic lobby/waiting-room logic is not considered product-stable
- after terminal call return, both peers can still end up recreating separate waiting rooms and remain stuck in parallel waiting state
- treat the current lobby implementation as a temporary baseline that is scheduled for redesign

The baseline transport assumptions are:

- Cloudflare Worker + Durable Objects
- HTTP polling signaling
- non-trickle ICE
- direct route first
- STUN by default
- TURN integration through `StreamingAssets/dev-secrets.json`
- `relayOnly` can force `RTCIceTransportPolicy.Relay` for explicit TURN testing

## What Is Implemented

- `AppBootstrap` as guarded composition root for `game.unity`
- presentation views instead of UI-driven business logic
- `RoomFlowCoordinator` for auto-lobby and room operations
- `RoomHeartbeatService` for waiting-room presence
- `ConnectionFlowCoordinator` for WebRTC session orchestration
- lifecycle-only FSM
- `ConnectionSession` and `ConnectionSnapshot`
- `WebRtcStatsSampler`
- `ConnectionPolicy`
- first downgrade path: `AudioOnly -> DataOnly`
- `RecoveryCoordinator`
- ICE restart recovery
- push-to-talk voice UX
- TURN integration with Metered static credentials
- relay/direct diagnostics through selected ICE route logging
- crash-reporting pipeline with local persistence and runtime fatal overlay

## What Is Not Finished Yet

- polished multi-device GUI/UX
- stable lobby/pairing UX; current semi-automatic lobby remains a known weak spot`n- relay-aware media policy such as auto-disable video on relay path
- manual signaling mode in the new stack
- WebSocket control channel for room events
- finalized best-effort Android local notifications after WebSocket control migration
- remote crash-report upload or email delivery
- native/IL2CPP crash capture beyond managed exception guards

## Main Runtime Model

Lifecycle is tracked separately from media and route.

- `ConnectionLifecycleState`
  - `Idle`
  - `Preparing`
  - `Signaling`
  - `Connecting`
  - `Connected`
  - `Recovering`
  - `Failed`
  - `Closed`
- `MediaMode`
  - `AudioOnly`
  - `DataOnly`
  - `Full` (reserved for future video)
- `RouteMode`
  - `Direct`
  - `Relay`
  - `ManualBootstrap`
- `SignalingMode`
  - `WorkerPolling`
  - `WorkerWebSocket`
  - `Manual`

## Key Files

- `Assets/Scripts/Bootstrap/AppBootstrap.cs`
- `Assets/Scripts/Application/Room/RoomFlowCoordinator.cs`
- `Assets/Scripts/Application/Room/RoomHeartbeatService.cs`
- `Assets/Scripts/Application/Connection/ConnectionFlowCoordinator.cs`
- `Assets/Scripts/Application/Connection/ConnectionSession.cs`
- `Assets/Scripts/Application/Connection/RecoveryCoordinator.cs`
- `Assets/Scripts/Transport/WorkerClient.cs`
- `Assets/Scripts/Transport/WebRtcPeerAdapter.cs`
- `Assets/Scripts/Transport/MediaCaptureService.cs`
- `Assets/Scripts/Transport/WebRtcStatsSampler.cs`
- `Assets/Scripts/CrashHandling/CrashCoordinator.cs`
- `Assets/Scripts/CrashHandling/FatalErrorView.cs`
- `Assets/Scripts/Server/worker.js`

## Next Priorities

1. Polish lobby and call-screen UX across devices.
2. Migrate room/control fast-path from polling to a Durable Object WebSocket channel.
3. Finalize best-effort Android local notifications after the WebSocket control channel lands.
4. Decide whether relay path should force audio + chat only.
5. Improve crash-screen wording and error-code readability for field testing.

## Voice UX Note

Current voice UX is push-to-talk.

Current application-level signaling over the DataChannel already includes chat and
lightweight live commands. Future improvements can extend that channel for richer
peer-speaking indicators without moving those UX signals back into the Worker.
