# Implementation Plan

## Status Summary

This file tracks the actual project path, not an aspirational rewrite.

The codebase now has a working booth-first communication baseline in `BT2.unity`.
The transport stack and TURN integration from earlier stages remain core assets and
are carried forward into the current product model.

Current branch posture:

- booth-first manual dialing is the active product path
- crash guard, WebRTC transport and TURN integration are working baseline assets
- the old room-first client path was removed from this branch after the booth migration stabilized
- future work should evolve the booth model, not revive waiting-room behavior

## Completed Stages

### Stage 0. Legacy Prototype

Status: done

Reference:

- `Assets/Scripts/ManualSignaling.cs`
- `Assets/Scenes/SampleScene.unity`

What it provided:

- a known-working WebRTC baseline
- manual signaling reference behavior
- proof that the transport idea works in practice

### Stage 1. New Scene Bootstrap and Happy Path

Status: done

Delivered:

- application bootstrap scene
- `AppBootstrap`
- view-based UI composition
- audio call
- chat over DataChannel
- hangup

### Stage 2. State Model Migration

Status: done

Delivered:

- `ConnectionSession`
- `ConnectionSnapshot`
- lifecycle-only FSM
- separation of lifecycle from media/route/signaling

### Stage 3. Quality Sampling and First Degradation

Status: done

Delivered:

- `WebRtcStatsSampler`
- `QualitySnapshot`
- `ConnectionPolicy`
- downgrade path `AudioOnly -> DataOnly`

Current limitation:

- `DataOnly` is still a pragmatic MVP approximation, not a fully renegotiated transport-mode switch

### Stage 4. Recovery Baseline

Status: archived baseline, later simplified

Delivered earlier:

- `RecoveryCoordinator`
- disconnected grace period
- retry budget and backoff
- ICE restart recovery

Current note:

- the branch no longer relies on the full recovery path during normal product flow
- current behavior keeps only a short `ICE Disconnected` grace period before terminal failure

### Stage 5. Durable Objects Migration

Status: done

Delivered:

- Cloudflare Worker signaling migrated from KV to Durable Objects
- signaling/session state moved into Durable Object storage
- Worker-backed control flow became the new baseline
- signaling delays caused by KV visibility were removed from the happy path

### Stage 6. Crash Guard and Fatal Error Screen

Status: done

Delivered:

- startup preflight checks in `AppBootstrap`
- guarded bootstrap stages with centralized fatal handling
- local crash-report model and sink
- global managed exception hooks
- runtime fatal overlay independent from normal scene UI
- local persistence of latest crash report for device-side diagnostics

Current limitation:

- native plugin or IL2CPP hard crashes are outside this managed guardrail

### Stage 7. Relay Fallback and TURN Hardening Baseline

Status: done enough for current branch

Delivered:

- Metered static TURN credentials wired through `dev-secrets.json`
- relay candidates gathered in practice
- explicit `relayOnly` remains available for testing
- relay fallback is queued for the next attempt after selected direct-path failures
- selected ICE route is logged from the nominated pair

Remaining work:

- decide final relay media policy
- document operator playbook for TURN credentials and quota monitoring

### Stage 8. Simplified Telephone Booth Architecture

Status: working baseline

Goals achieved:

- removed lobby, waiting-room and heartbeat from the product flow
- gave every install one stable booth number
- kept only minimal persistent ownership registry on the server
- derived online/offline only from a live booth WebSocket
- made call setup explicit and manual: dial, ring, accept, reject, hangup
- kept signaling payloads on HTTP while moving control events to booth socket
- preserved the existing adaptive WebRTC transport stack

Delivered in code and manually verified on the branch:

- `BoothFlowCoordinator`
- `BoothSocketService`
- stable 4-digit booth-number assignment from local identity in the current test build
- Worker booth registration endpoint
- Worker booth event WebSocket endpoint
- dial / accept / reject / hangup / connected endpoints
- booth line snapshot and remote hangup propagation
- active `BT2.unity` screen-layer UI
- line-state sync to `in_call`
- local incoming-call notifications when app is not in focus but socket is alive
- legacy room-flow client classes removed from the branch

Current known caveats:

- stale active-call cleanup after extreme simultaneous crash/offline cases still needs hardening
- local notifications are best-effort only and are not a reliable background incoming-call solution
- server-side Durable Object class names still reflect their migration origin

## Deferred / Next Work

### Stage 9. FCM Push Wakeup

Status: next branch target

Goals:

- add reliable Android background incoming-call wakeup through FCM
- keep booth socket as the foreground control plane
- keep HTTP signaling slots and WebRTC transport unchanged where possible

### Stage 10. Mobile Notification Polish

Status: partially landed, not finalized

Goals:

- incoming-call notification polish
- better wording and timing for connected/incoming notifications
- platform-specific UX cleanup after booth flow is verified

### Stage 11. Video Policy and UX

Status: deferred

Reason:

- video multiplies complexity in quality management, degradation and UX
- the audio/data baseline should remain stable before adding more aggressive video logic

### Stage 12. Booth UX Extensions

Status: deferred

Possible future work:

- contacts or favorites
- alias or display label for a booth number
- voicemail or short text fallback
- multi-device ownership

These are not part of v1.

## Notes for Future Iterations

- keep crash diagnostics local-first unless there is a strong reason to add backend upload
- keep booth presence socket-based; do not reintroduce heartbeat unless there is a proven product need
- preserve the current transport resilience work instead of rebuilding signaling/media layers from scratch
- after any booth socket reconnect, prefer server `line_snapshot` over stale local UI assumptions
- use FCM only as wakeup/notification transport, not as a replacement for booth socket or signaling slots
- if useful, limited Sonnet budget can be used via the user for scoped code writing, review or refactoring; do not assume direct connector access
