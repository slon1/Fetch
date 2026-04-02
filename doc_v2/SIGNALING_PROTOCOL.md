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

Important limitation:

- booth socket is reliable for foreground/live control
- it is not a reliable background wakeup mechanism on mobile by itself
- reliable background incoming-call wakeup is planned through FCM in the next branch

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
  "boothNumber": "3661",
  "ownerClientId": "stable-client-id"
}
```

Successful response shape:

```json
{
  "ok": true,
  "boothNumber": "3661",
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
  "boothNumber": "3661",
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
  "callerNumber": "3661",
  "targetNumber": "4827"
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
  "callerNumber": "3661",
  "calleeNumber": "4827"
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
  "boothNumber": "4827"
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
  "boothNumber": "4827"
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
  "boothNumber": "3661"
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
  "boothNumber": "3661"
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

### Delete Signal

- `DELETE /api/signal/{callId}?type={type}`

Used for best-effort cleanup after the signal is consumed.
