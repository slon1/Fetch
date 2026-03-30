# Implementation Plan

## Status Summary

This file tracks the actual project path, not an aspirational rewrite.

The codebase already has a working vertical slice in `game.unity`, so the next
iterations should build on that reality instead of restarting architecture from
scratch.

Current branch posture:

- media, signaling, recovery and crash-guard baseline are considered working enough to archive as a milestone
- the current semi-automatic lobby is explicitly not the final product model
- known lobby failure mode: after a call ends, both peers can recreate separate waiting rooms and remain stuck in parallel waiting state
- next iteration is expected to redesign pairing/lobby UX rather than continue stacking fixes onto the current room-first flow

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

- `game.unity` as new application scene
- `AppBootstrap`
- view-based UI composition
- room list/create/join baseline
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
- first downgrade path: `AudioOnly -> DataOnly`

Current limitation:

- `DataOnly` is still a pragmatic MVP approximation, not a fully renegotiated
  transport-mode switch

### Stage 4. Recovery Baseline

Status: done

Delivered:

- `RecoveryCoordinator`
- disconnected grace period
- retry budget and backoff
- ICE restart recovery

Current tested result:

- short network loss can recover within the same session
- longer outages end in `Failed` and return to lobby

### Stage 5. Durable Objects Migration

Status: done

Delivered:

- Cloudflare Worker signaling migrated from KV to Durable Objects
- room state stored in `RoomDurableObject`
- lobby registry handled by `LobbyDurableObject`
- `/api/rooms/{id}` and `/api/rooms/{id}/heartbeat` support
- signaling delays caused by KV visibility removed from the happy path

### Stage 6. Auto-Lobby and Waiting Room Presence

Status: done

Delivered:

- auto-lobby bootstrap on app start
- own waiting room hidden from own room list
- create-own-room flow when no foreign room exists
- join-first flow when foreign rooms exist
- waiting-room heartbeat through `RoomHeartbeatService`
- return to lobby after terminal session states using the same bootstrap logic

### Stage 7. Crash Guard and Fatal Error Screen

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

## Active Short-Term Work

### Stage 8. WebSocket Control Channel and Reduced Heartbeat Role

Status: partially landed, not accepted as product baseline

Goals:

- move low-latency room/control events from polling to a Durable Object WebSocket channel
- keep heartbeat only as a coarse waiting-room lease / cleanup mechanism
- remove fast-path dependence on room polling for `peer_joined` and control wakeups

Acceptance:

- waiting-room owner is notified about `peer_joined` without room polling delay
- remote hangup and room-close events no longer depend on polling cadence
- heartbeat interval can be relaxed because it is no longer on the fast path

Current caveat before redesign:

- first WebSocket/control pieces may exist in the branch, but the surrounding semi-automatic lobby behavior is still considered unstable
- Stage 8 should not be treated as complete until the next pairing/lobby UX redesign lands

### Stage 9. TURN Operational Hardening

Status: in progress

Goals:

- treat TURN as a real deploy/test milestone, not a placeholder
- verify credentials flow in practice
- test harder NAT and blocked direct-path conditions
- decide whether relay path should automatically downgrade media policy

Acceptance:

- TURN path is verified end to end
- operator setup is documented
- direct vs relay behavior is observable in logs

Current progress:

- Metered static TURN credentials are wired through `dev-secrets.json`
- relay candidates are gathered
- forced relay mode works through `relayOnly`
- selected ICE route is logged from the nominated pair
- direct and relay runs are both exercised against the Durable Objects backend

Remaining work:

- add automatic relay fallback for the next connection attempt after direct/recovery failure
- decide final relay media policy
- document operator playbook for TURN credentials and quota monitoring
- optionally add clearer user-facing relay/direct indicator later

## Deferred Work

### Stage 10. Video Policy and UX

Status: deferred

Reason:

- video multiplies complexity in quality management, degradation and UX
- the audio/data baseline should remain stable before adding more aggressive video logic

### Stage 11. Manual Mode in New Stack

Status: deferred

Goals:

- manual SDP export/import in the new architecture
- dedicated UI for manual bootstrap

Reason:

- current lobby, crash guard and TURN work are more valuable first

### Stage 12. Android Local Notifications Finalization

Status: deferred until Stage 8 lands

Reason:

- `Connected` can be notified locally today, but `PeerJoined` should use the future WebSocket control channel instead of polling/heartbeat
- final best-effort notifications should be wired after the room/control socket exists

### Stage 13. Larger Room Models / SFU Path

Status: deferred research

Reason:

- current product baseline is still 1:1
- larger rooms and host-to-many media delivery likely require a separate SFU/media strategy

## Notes for Future Iterations

- keep crash diagnostics local-first unless there is a strong reason to add backend upload
- prefer incremental DO-backed improvements over rewrites of the entire signaling path
- do not let GUI refactors blur room, signaling and connection responsibilities
- if useful, limited Sonnet budget can be used via the user for scoped code writing, review or refactoring; do not assume direct connector access
