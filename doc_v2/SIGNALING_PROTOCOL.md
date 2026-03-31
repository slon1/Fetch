# Signaling Protocol

## Current Protocol Scope

The active protocol is the booth-first signaling contract used by the current v2
stack.

Characteristics:

- Cloudflare Worker backend with Durable Objects
- one booth number per install
- one booth WebSocket per active client
- booth control events over WebSocket
- SDP and signaling slots over HTTP
- non-trickle ICE

This split is intentional. Low-latency line events use the booth socket, while
retriable signaling payloads remain on HTTP.

## High-Level Model

There are two server-side domains:

- booth domain: ownership, presence, line state and incoming/outgoing call control
- call domain: one durable call record plus signaling slots keyed by `callId`

Current product flow:

1. client registers or confirms booth ownership
2. client opens `GET /api/booths/{number}/events` WebSocket
3. caller sends `POST /api/dial`
4. callee receives `incoming_call` via booth socket
5. callee manually accepts or rejects
6. caller becomes offerer and posts `offer`
7. callee reads `offer`, posts `answer`, and WebRTC connects
8. hangup and line reset are propagated through booth socket plus signaling cleanup

## Booth API

### Register Booth

- `POST /api/booths/register`

Body:

```json
{
  "boothNumber": "783999593661",
  "ownerClientId": "stable-client-id"
}
```

Successful response shape:

```json
{
  "ok": true,
  "boothNumber": "783999593661",
  "ownerClientId": "stable-client-id",
  "createdAt": 1774890000000,
  "lastSeenAt": 1774890000000
}
```

Semantics:

- idempotent for the same `boothNumber + ownerClientId`
- rejects booth ownership conflicts
- stores minimal persistent ownership record only

### Booth Events WebSocket

- `GET /api/booths/{number}/events`
- HTTP upgrade to WebSocket

Current event types:

- `line_snapshot`
- `incoming_call`
- `outgoing_ringing`
- `call_accepted`
- `call_rejected`
- `remote_hangup`
- `busy`
- `offline`
- `line_reset`

Current behavior:

- the booth socket is the source of truth for line reachability
- after reconnect, the server sends a fresh `line_snapshot`
- client UI should trust that snapshot over stale local assumptions

### Booth Snapshot Shape

Logical snapshot shape:

```json
{
  "type": "line_snapshot",
  "boothNumber": "783999593661",
  "lineState": "idle",
  "peerNumber": null,
  "call": null,
  "message": null,
  "sentAt": 1774890000000
}
```

When a call is pending or active, `call` contains the current `callId` plus caller
and callee booth numbers.

## Call Control API

### Dial

- `POST /api/dial`

Body:

```json
{
  "callerNumber": "783999593661",
  "targetNumber": "123456789012"
}
```

Possible response outcomes:

- `ringing`
- `not_registered`
- `offline`
- `busy`

Example success response:

```json
{
  "ok": true,
  "result": "ringing",
  "callId": "call-id",
  "callerNumber": "783999593661",
  "calleeNumber": "123456789012"
}
```

Notes:

- caller does not start WebRTC yet; it waits for callee accept
- if both users dial each other nearly simultaneously, the switchboard should avoid a user-facing failure and converge onto one pending call

### Accept Call

- `POST /api/calls/{callId}/accept`

Body:

```json
{
  "boothNumber": "123456789012"
}
```

Semantics:

- validates that the accepting booth matches the callee for the pending call
- caller receives `call_accepted` through booth socket
- caller becomes offerer
- callee becomes answerer

### Reject Call

- `POST /api/calls/{callId}/reject`

Body:

```json
{
  "boothNumber": "123456789012"
}
```

Semantics:

- terminates the pending call
- caller receives `call_rejected` through booth socket
- both booths return to `idle`

### Hang Up

- `POST /api/calls/{callId}/hangup`

Body:

```json
{
  "boothNumber": "783999593661"
}
```

Semantics:

- valid for either participant
- both booths return to `idle`
- remote side receives `remote_hangup`
- signaling slots are cleaned up best-effort after terminal call teardown

### Mark Connected

- `POST /api/calls/{callId}/connected`

Body:

```json
{
  "boothNumber": "783999593661"
}
```

Semantics:

- transitions booth line state from `connecting` to `in_call`
- used to keep booth line state aligned with real WebRTC connection state

## Signaling Slot API

### Post Signal

- `POST /api/signal/{callId}`

Stores a signaling envelope in the slot for `(callId, type)`.

### Get Signal

- `GET /api/signal/{callId}?type={type}`

Current behavior:

- returns envelope JSON if slot exists
- returns `404` if slot is empty
- client treats missing slot as a normal polling condition

### Delete Signal

- `DELETE /api/signal/{callId}?type={type}`

Used as best-effort cleanup for consumed signaling slots.

## Current Signal Types

- `offer`
- `answer`
- `hangup`

Current implementation also reuses the same `offer` and `answer` slot types for
ICE restart instead of introducing restart-specific message types.

## Envelope Shape

Current envelope fields:

```json
{
  "sessionId": "call-id",
  "fromPeerId": "peer-id",
  "toPeerId": "optional-peer-id",
  "messageId": "unique-id",
  "type": "offer",
  "ttlMs": 60000,
  "payloadJson": "{...}",
  "sentAt": 1774890000000
}
```

Notes:

- the envelope still uses the historical field name `sessionId`
- in the booth architecture this value now corresponds to `callId`

## SDP Payload

For `offer` and `answer`, `payloadJson` currently contains SDP-only payload.

Example logical shape:

```json
{
  "sdp": "v=0..."
}
```

Because the stack is non-trickle, local SDP is posted only after ICE gathering
finishes.

## Polling Rules

Polling still exists, but only for signaling slots.

Client behavior:

- poll for `offer`, `answer`, or `hangup` by `callId`
- tolerate `404` as "not yet available"
- stop polling on timeout or cancellation

This is especially important for:

- caller waiting for `answer`
- callee waiting for initial `offer`
- either side watching for `hangup` during fallback cases

## ICE Restart Semantics

Current recovery flow:

- caller-side recovery produces a new offer with `RTCOfferAnswerOptions.iceRestart = true`
- offer is posted into the normal `offer` slot for the same `callId`
- callee reads it, applies it, produces answer, and posts to normal `answer` slot
- both sides wait for ICE to return to `Connected` or `Completed`

Current simplifications:

- caller initiates restart
- no glare handling
- no peer recreation by default
- no separate restart-specific message types

## Presence Semantics

Current product semantics:

- `not_registered`: no booth ownership record exists
- `offline`: booth exists but no live booth socket is connected
- `busy`: booth exists and line state is not `idle`
- `ringing`: dial accepted by the switchboard and the target booth is being alerted

There is no heartbeat in the booth architecture. Presence is derived only from the
live booth socket.

## Future Extensions

Possible later protocol additions:

- contacts or favorites
- alias or display label for booth numbers
- voicemail or short text fallback
- explicit restart-specific signaling message types if debugging value outweighs added complexity

These are not required for the current working baseline.
