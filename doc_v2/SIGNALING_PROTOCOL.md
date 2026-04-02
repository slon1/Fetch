# Signaling Protocol

## Current Protocol Scope

The active protocol is the booth-first signaling contract used by the current v2
stack.

Characteristics:

- Cloudflare Worker backend with Durable Objects
- one booth number per install
- one booth WebSocket per active foreground client
- one booth FCM token per booth in v1
- booth control events over WebSocket
- Android background wakeup through FCM push
- SDP and signaling slots over HTTP
- non-trickle ICE

This split is intentional. Low-latency line events use the booth socket, while
retriable signaling payloads remain on HTTP.

Important rule:

- FCM complements booth socket for Android wakeup
- FCM is not the source of truth for line state
- after wakeup, the app must reconnect booth socket and trust the server `line_snapshot`

## High-Level Model

There are two server-side domains:

- booth domain: ownership, presence, line state, push token and incoming/outgoing call control
- call domain: one durable call record plus signaling slots keyed by `callId`

Current product flow:

1. client registers or confirms booth ownership
2. Android client initializes Firebase Messaging and syncs push token when available
3. client opens `GET /api/booths/{number}/events` WebSocket
4. caller sends `POST /api/dial`
5. callee receives `incoming_call` via booth socket if foreground, or FCM wakeup if background
6. callee manually accepts or rejects
7. caller becomes offerer and posts `offer`
8. callee reads `offer`, posts `answer`, and WebRTC connects
9. hangup and line reset are propagated through booth socket plus signaling cleanup

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

### Register Booth Push Token

- `POST /api/booths/{number}/push-token`

Body:

```json
{
  "clientId": "stable-client-id",
  "platform": "android",
  "token": "fcm-registration-token"
}
```

Semantics:

- validates booth ownership by `clientId`
- stores or replaces the booth's current Android FCM token
- idempotent for the same token
- rejects invalid token body or ownership mismatch

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

- the booth socket is the source of truth for line reachability in foreground
- after reconnect, the server sends a fresh `line_snapshot`
- client UI should trust that snapshot over stale local assumptions

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

Notes:

- caller does not start WebRTC yet; it waits for callee accept
- if target booth has no active socket but does have a valid FCM token, the worker may still start the ringing flow and deliver wakeup through FCM
- if FCM delivery is rejected as invalid and there is no live socket, the worker rolls the call back to `offline`

### Accept Call

- `POST /api/calls/{callId}/accept`

Semantics:

- validates that the accepting booth matches the callee for the pending call
- caller receives `call_accepted` through booth socket
- caller becomes offerer
- callee becomes answerer

### Reject Call

- `POST /api/calls/{callId}/reject`

Semantics:

- terminates the pending call
- caller receives `call_rejected` through booth socket
- both booths return to `idle`

### Hang Up

- `POST /api/calls/{callId}/hangup`

Semantics:

- valid for either participant
- both booths return to `idle`
- remote side receives `remote_hangup`
- signaling slots are cleaned up best-effort after terminal call teardown

### Mark Connected

- `POST /api/calls/{callId}/connected`

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

## FCM Delivery Notes

Worker FCM behavior in the current branch:

- uses FCM HTTP v1
- authenticates with service-account credentials stored in Cloudflare secrets
- sends high-priority Android incoming-call wakeup
- uses short `ttl` and collapse behavior to avoid stale or duplicated ringing notifications
- keeps FCM limited to incoming-call wakeup, not ongoing booth/call control
