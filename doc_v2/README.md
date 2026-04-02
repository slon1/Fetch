# WebRTC v2 Docs

## Purpose

This folder documents the current Unity 1:1 messenger baseline.

The product target is a resilient communication app for degraded or partially
blocked networks, not a generic video-call demo.

## Current Product Baseline

- one stable 4-digit booth number per install in the current test build
- manual dialing instead of room discovery
- live booth reachability through a Durable Object WebSocket in foreground
- FCM push for reliable Android incoming-call wakeup in background
- HTTP signaling slots for SDP, answer and hangup envelopes
- adaptive WebRTC transport with direct-first policy and relay fallback
- local-first diagnostics through crash guard and detailed logs
- active scene path reduced to `BT2.unity`

## Current Working Flow

As of this branch, the active runtime flow is booth-first in `BT2.unity`.

- register or confirm local booth number on app start
- initialize Firebase Messaging on Android
- sync booth FCM token to the worker when available
- connect booth control socket
- show own booth number
- manually dial another number
- receive one of: `not_registered`, `offline`, `busy`, `ringing`
- incoming call with manual `accept` / `reject`
- foreground incoming control through booth socket
- background incoming wakeup through FCM push
- caller starts as offerer
- callee starts as answerer
- audio + video call
- chat over `RTCDataChannel`
- hangup
- short grace period on `ICE Disconnected`, then terminal failure if link does not return
- fatal startup/runtime error screen for managed failures

The old room/lobby implementation is no longer part of the client path.
Legacy room-flow classes and old reference scenes were removed from this branch.

## What Is Implemented

- `AppBootstrap` as guarded composition root for `BT2.unity`
- `BoothFlowCoordinator` for booth registration, dialing and line state
- `BoothSocketService` for low-latency booth events
- `FcmPushService` for Android token lifecycle and incoming push handling
- `ConnectionFlowCoordinator` for WebRTC session orchestration
- lifecycle-only FSM
- `ConnectionSession` and `ConnectionSnapshot`
- `WebRtcStatsSampler`
- `ConnectionPolicy`
- downgrade path `AudioOnly -> DataOnly`
- push-to-talk voice UX
- active camera/video path
- TURN integration with Metered static credentials
- relay/direct diagnostics through selected ICE route logging
- crash-reporting pipeline with local persistence and runtime fatal overlay
- direct Android notification helper for local notification rendering

## What Is Not Finished Yet

- stronger stale line cleanup for extreme crash / simultaneous-offline cases
- relay-aware media policy such as auto-disable video on relay path
- remote crash-report upload or email delivery
- native/IL2CPP crash capture beyond managed exception guards
- future booth extensions like contacts, aliases, voicemail or multi-device ownership
- historical server-side class names such as `LobbyDurableObject` and `RoomDurableObject`

## Main Runtime Model

Lifecycle is tracked separately from booth line state, media and route.

- `ConnectionLifecycleState`
  - `Idle`
  - `Preparing`
  - `Signaling`
  - `Connecting`
  - `Connected`
  - `Failed`
  - `Closed`
- `BoothLineState`
  - `Idle`
  - `Dialing`
  - `RingingOutgoing`
  - `RingingIncoming`
  - `Connecting`
  - `InCall`
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

## Key Files

- `Assets/Scripts/Bootstrap/AppBootstrap.cs`
- `Assets/Scripts/Application/Booth/IBoothFlow.cs`
- `Assets/Scripts/Application/Booth/BoothFlowCoordinator.cs`
- `Assets/Scripts/Application/Connection/ConnectionFlowCoordinator.cs`
- `Assets/Scripts/Transport/BoothSocketService.cs`
- `Assets/Scripts/Transport/WorkerClient.cs`
- `Assets/Scripts/Transport/WebRtcPeerAdapter.cs`
- `Assets/Scripts/Transport/MediaCaptureService.cs`
- `Assets/Scripts/Shared/FcmPushService.cs`
- `Assets/Scripts/CrashHandling/CrashCoordinator.cs`
- `Assets/Scripts/Server/worker.js`
- `Assets/Scenes/BT2.unity`
- `Assets/Plugins/Android/AndroidManifest.xml`
- `wrangler.toml`

## Current Mobile Notes

- Android now requires `CAMERA`, `RECORD_AUDIO` and `POST_NOTIFICATIONS` for the full booth experience
- FCM requires Google Play services on the device
- booth socket remains the foreground control plane; FCM does not replace it
- local Android notifications are now rendered through a direct Android bridge instead of `com.unity.mobile.notifications`

## Next Priorities

1. Harden stale `connecting` / `in_call` cleanup after extreme failures.
2. Decide whether relay path should force audio + chat only.
3. Improve TURN/route diagnostics so direct vs relay usage is easier to explain.
4. Rename remaining historical worker identifiers when it is safe.
5. Decide whether multi-device booth ownership is worth supporting.

## Voice UX Note

Current voice UX is push-to-talk.

Current application-level signaling over the DataChannel already includes chat and
lightweight live commands. Future improvements can extend that channel for richer
peer-speaking indicators without moving those UX signals back into the Worker.
