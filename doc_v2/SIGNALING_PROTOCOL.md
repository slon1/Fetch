# Signaling Protocol

## Current Protocol Scope

The current protocol is the MVP signaling contract used by the new v2 stack.

Characteristics:

- Cloudflare Worker backend
- KV-backed room index and signaling slots
- HTTP polling
- non-trickle ICE

This is enough for the current audio/data baseline and for ICE restart recovery.

## Room API

### List Rooms

- `GET /api/rooms`

Returns only rooms in `waiting` state.

### Create Room

- `POST /api/rooms`

Body:

```json
{
  "displayName": "Alice",
  "sessionId": "session-id",
  "creatorPeerId": "peer-id"
}
```

### Join Room

- `POST /api/rooms/{sessionId}/join`

Returns:

```json
{
  "ok": true,
  "sessionId": "session-id",
  "callerPeerId": "creator-peer-id"
}
```

### Delete Room

- `DELETE /api/rooms/{sessionId}`

Used when the room should be removed from the lobby index.

## Signaling API

### Post Signal

- `POST /api/signal/{sessionId}`

Stores a signaling envelope in the slot for `(sessionId, type)`.

### Get Signal

- `GET /api/signal/{sessionId}?type={type}`

Current behavior:

- returns envelope JSON if slot exists
- returns `404` if slot is empty
- client treats missing slot as a normal polling condition

### Delete Signal

- `DELETE /api/signal/{sessionId}?type={type}`

Used as best-effort cleanup for consumed signaling slots.

## Current Signal Types

- `offer`
- `answer`
- `hangup`

Current implementation also uses the same `offer` / `answer` types for ICE
restart instead of introducing separate restart-only message types.

## Envelope Shape

Current envelope fields:

```json
{
  "sessionId": "session-id",
  "fromPeerId": "peer-id",
  "toPeerId": "optional-peer-id",
  "messageId": "unique-id",
  "type": "offer",
  "ttlMs": 60000,
  "payloadJson": "{...}",
  "sentAt": 1710000000000
}
```

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

Client behavior:

- poll for `offer`, `answer`, or `hangup`
- tolerate 404 as "not yet available"
- stop polling on timeout or cancellation

This is critical for the caller waiting for `answer`: absence of an answer is
not an error until the polling timeout expires.

## ICE Restart Semantics

Current recovery flow:

- creator initiates ICE restart by producing a new offer with
  `RTCOfferAnswerOptions.iceRestart = true`
- offer is posted into the normal `offer` slot
- callee reads it, applies it, produces answer, and posts to normal `answer`
  slot
- both sides wait for ICE to return to `Connected` or `Completed`

Current simplifications:

- caller is the initiator
- no glare handling
- no peer recreation
- no separate restart-specific message types

## Future Extensions

Possible later protocol additions:

- WebSocket signaling
- trickle ICE queue instead of one-slot signaling
- manual bootstrap blob exchange
- explicit restart-specific message types if debugging value outweighs added
  protocol complexity

These are not required for the current working baseline.
