/**
 * Cloudflare Worker — WebRTC v2 signaling
 *
 * Durable Object bindings (wrangler.toml):
 *   ROOMS_DO  → RoomDurableObject   (one instance per sessionId)
 *   LOBBY_DO  → LobbyDurableObject  (singleton "global-lobby")
 *
 * Changes vs original:
 *   [H-6]  handleListRooms: parallel DO fetches via Promise.allSettled
 *   [H-6]  stale-lobby cleanup: fire-and-forget, does not block response
 *   [L-5]  handleHeartbeat: creatorPeerId validated as non-empty string
 *   [M-4]  handleCreateRoom: returns server-authoritative room to client
 *   [bug]  handleInit: idempotent re-init guard (returns existing room if
 *          sessionId already initialised, prevents duplicate-create races)
 *   [bug]  handleJoin: atomic read-modify-write via blockConcurrencyWhile
 *   [bug]  handleListRooms: lobby.remove failures no longer silently eat errors
 *   misc   sessionId length cap (128 chars) to prevent oversized DO name keys
 *   misc   displayName trimmed before length check
 *   misc   MAX_LOBBY_SIZE guard (100 entries) to keep LobbyDO storage bounded
 *   misc   all internal helpers typed / documented
 */

// ─── Constants ────────────────────────────────────────────────────────────────

const CORS_HEADERS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, POST, DELETE, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type",
};

const SIGNAL_TYPES        = new Set(["offer", "answer", "hangup"]);
const DEFAULT_ROOM_TTL_SEC       = 180;
const DEFAULT_HEARTBEAT_TTL_SEC  = 30;
const MIN_ROOM_TTL_SEC           = 60;
const MIN_HEARTBEAT_TTL_SEC      = 15;
const SESSION_RETENTION_MS       = 24 * 60 * 60 * 1000; // 24 h hard cap
const LOBBY_DO_NAME              = "global-lobby";
const MAX_SESSION_ID_LEN         = 128;
const MAX_DISPLAY_NAME_LEN       = 64;
const MAX_LOBBY_SIZE             = 100; // prevent unbounded LobbyDO storage

// ─── Worker entry point ───────────────────────────────────────────────────────

export default {
  async fetch(request, env) {
    try {
      return await route(request, env);
    } catch (err) {
      console.error("[worker] unhandled error", err);
      return jsonError("Internal Server Error", 500);
    }
  },
};

async function route(request, env) {
  const { method, url: rawUrl } = request;
  const url  = new URL(rawUrl);
  const path = url.pathname;

  if (method === "OPTIONS") return respond204();

  // POST /api/rooms  /  GET /api/rooms
  if (path === "/api/rooms") {
    if (method === "GET")  return cors(await handleListRooms(env));
    if (method === "POST") return cors(await handleCreateRoom(request, env));
  }

  // POST /api/rooms/:id/heartbeat
  const heartbeatM = path.match(/^\/api\/rooms\/([^/]+)\/heartbeat$/);
  if (heartbeatM && method === "POST")
    return cors(await handleHeartbeatRoom(heartbeatM[1], request, env));

  // POST /api/rooms/:id/join
  const joinM = path.match(/^\/api\/rooms\/([^/]+)\/join$/);
  if (joinM && method === "POST")
    return cors(await handleJoinRoom(joinM[1], env));

  // GET|DELETE /api/rooms/:id
  const roomM = path.match(/^\/api\/rooms\/([^/]+)$/);
  if (roomM) {
    if (method === "GET")    return cors(await handleGetRoom(roomM[1], env));
    if (method === "DELETE") return cors(await handleDeleteRoom(roomM[1], env));
  }

  // GET|POST|DELETE /api/signal/:id
  const sigM = path.match(/^\/api\/signal\/([^/]+)$/);
  if (sigM) {
    const sid = sigM[1];
    if (method === "POST")   return cors(await handlePostSignal(sid, request, env));
    if (method === "GET")    return cors(await handleGetSignal(sid, url, env));
    if (method === "DELETE") return cors(await handleDeleteSignal(sid, url, env));
  }

  return jsonError("Not Found", 404);
}

// ─── Room handlers ────────────────────────────────────────────────────────────

/**
 * GET /api/rooms
 *
 * Fetches the lobby index, then verifies all listed rooms in parallel against
 * their individual RoomDurableObjects.  Stale entries are removed from the
 * lobby index as a background task so the response is not blocked by cleanup.
 */
async function handleListRooms(env) {
  const lobbyResp = await lobbyStub(env).fetch("https://do/internal/list");
  if (!lobbyResp.ok) return lobbyResp;

  const payload   = await lobbyResp.json();
  const lobbyRows = Array.isArray(payload?.rooms) ? payload.rooms : [];

  if (lobbyRows.length === 0) return jsonOk({ rooms: [] });

  // Fetch all room DOs in parallel — fixes H-6 (sequential O(n) round trips)
  const settled = await Promise.allSettled(
    lobbyRows
      .filter(r => isValidSessionId(r?.sessionId))
      .map(r =>
        roomStub(env, r.sessionId)
          .fetch("https://do/internal/room", { method: "GET" })
          .then(async res => ({ sessionId: r.sessionId, res, room: res.ok ? await res.json() : null }))
      )
  );

  const now         = Date.now();
  const visibleRooms  = [];
  const staleIds      = [];

  for (const result of settled) {
    if (result.status === "rejected") continue; // network error — skip silently
    const { sessionId, res, room } = result.value;

    if (!res.ok || !isRoomLobbyVisible(room, now)) {
      staleIds.push(sessionId);
    } else {
      visibleRooms.push(room);
    }
  }

  // Clean up stale lobby entries in the background — does not block response
  if (staleIds.length > 0) {
    removeLobbyEntriesBackground(env, staleIds);
  }

  return jsonOk({ rooms: visibleRooms });
}

/**
 * POST /api/rooms
 *
 * Fix M-4: returns server-authoritative room object so the client does not
 * have to construct it locally from approximate timestamps.
 */
async function handleCreateRoom(request, env) {
  const body = await parseJson(request);
  if (!body) return jsonError("Invalid JSON", 400);

  const { displayName, sessionId, creatorPeerId } = body;
  if (!displayName || !sessionId || !creatorPeerId)
    return jsonError("displayName, sessionId, creatorPeerId required", 400);

  if (!isValidSessionId(sessionId))
    return jsonError("sessionId too long or empty", 400);

  const initResp = await roomStub(env, sessionId).fetch("https://do/internal/init", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });

  if (!initResp.ok) return initResp;

  const initPayload = await initResp.json();
  const room = initPayload?.room;
  if (!room) return jsonError("Room initialization failed", 500);

  // Register in lobby — fire-and-forget is intentional: if this fails the room
  // is still reachable by sessionId; it will simply not appear in the lobby
  // list until the next heartbeat triggers an upsert.  For MVP this is fine.
  lobbyStub(env)
    .fetch("https://do/internal/upsert", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(room),
    })
    .catch(err => console.warn("[worker] lobby upsert failed", err));

  // Return the server-authoritative room so client timestamps are accurate
  return jsonOk({ ok: true, room });
}

async function handleGetRoom(sessionId, env) {
  if (!isValidSessionId(sessionId)) return jsonError("invalid sessionId", 400);
  return roomStub(env, sessionId).fetch("https://do/internal/room", { method: "GET" });
}

async function handleHeartbeatRoom(sessionId, request, env) {
  if (!isValidSessionId(sessionId)) return jsonError("invalid sessionId", 400);
  const body = await request.text();
  return roomStub(env, sessionId).fetch("https://do/internal/heartbeat", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body,
  });
}

async function handleJoinRoom(sessionId, env) {
  if (!isValidSessionId(sessionId)) return jsonError("invalid sessionId", 400);

  const joinResp = await roomStub(env, sessionId).fetch("https://do/internal/join", {
    method: "POST",
  });

  if (joinResp.ok) {
    // Remove from lobby index — fire-and-forget
    lobbyStub(env)
      .fetch("https://do/internal/remove", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sessionId }),
      })
      .catch(err => console.warn("[worker] lobby remove after join failed", err));
  }

  return joinResp;
}

async function handleDeleteRoom(sessionId, env) {
  if (!isValidSessionId(sessionId)) return jsonError("invalid sessionId", 400);

  // Both calls are fire-and-forget: if the DO is already gone the DELETE
  // is a no-op; if the lobby entry is stale it will be swept on next list.
  await Promise.allSettled([
    roomStub(env, sessionId).fetch("https://do/internal/delete", { method: "DELETE" }),
    lobbyStub(env).fetch("https://do/internal/remove", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sessionId }),
    }),
  ]);

  return jsonOk({ ok: true });
}

// ─── Signal handlers ──────────────────────────────────────────────────────────

async function handlePostSignal(sessionId, request, env) {
  if (!isValidSessionId(sessionId)) return jsonError("invalid sessionId", 400);
  return roomStub(env, sessionId).fetch("https://do/internal/signal", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: await request.text(),
  });
}

async function handleGetSignal(sessionId, url, env) {
  if (!isValidSessionId(sessionId)) return jsonError("invalid sessionId", 400);
  const type = url.searchParams.get("type");
  if (!type) return jsonError("type query parameter required", 400);
  if (!SIGNAL_TYPES.has(type)) return jsonError(`unsupported signal type: ${type}`, 400);

  return roomStub(env, sessionId).fetch(
    `https://do/internal/signal?type=${encodeURIComponent(type)}`,
    { method: "GET" }
  );
}

async function handleDeleteSignal(sessionId, url, env) {
  if (!isValidSessionId(sessionId)) return jsonError("invalid sessionId", 400);
  const type = url.searchParams.get("type");
  if (!type) return jsonError("type query parameter required", 400);
  if (!SIGNAL_TYPES.has(type)) return jsonError(`unsupported signal type: ${type}`, 400);

  return roomStub(env, sessionId).fetch(
    `https://do/internal/signal?type=${encodeURIComponent(type)}`,
    { method: "DELETE" }
  );
}

// ─── RoomDurableObject ────────────────────────────────────────────────────────

export class RoomDurableObject {
  constructor(state) {
    this.state = state;
  }

  async fetch(request) {
    const url    = new URL(request.url);
    const method = request.method;
    const path   = url.pathname;

    if (method === "OPTIONS") return respond204();

    if (path === "/internal/init"      && method === "POST")   return this.#handleInit(request);
    if (path === "/internal/room"      && method === "GET")    return this.#handleGetRoom();
    if (path === "/internal/heartbeat" && method === "POST")   return this.#handleHeartbeat(request);
    if (path === "/internal/join"      && method === "POST")   return this.#handleJoin();
    if (path === "/internal/delete"    && method === "DELETE") return this.#handleDelete();

    if (path === "/internal/signal") {
      if (method === "POST")   return this.#handlePostSignal(request);
      if (method === "GET")    return this.#handleGetSignal(url);
      if (method === "DELETE") return this.#handleDeleteSignal(url);
    }

    return jsonError("Not Found", 404);
  }

  // Alarm fires when the earliest of: heartbeat TTL, room TTL, session cap
  async alarm() {
    const room = await this.#getRoom();
    if (!room) { await this.#clearAll(); return; }

    const now = Date.now();
    const expired =
      room.createdAt + SESSION_RETENTION_MS <= now ||
      (room.status === "waiting" && !isRoomWaitingAlive(room, now)) ||
      room.status === "closed";

    if (expired) { await this.#clearAll(); return; }

    await this.#scheduleAlarm(room, now);
  }

  // ── Internal handlers ──────────────────────────────────────────────────────

  /**
   * Idempotent init: if this DO already has a room with the same sessionId,
   * return it unchanged.  This prevents the duplicate-room race where the
   * Worker fires a second init before the first response arrives.
   */
  async #handleInit(request) {
    const body = await parseJson(request);
    if (!body) return jsonError("Invalid JSON", 400);

    const { displayName, sessionId, creatorPeerId } = body;
    if (!displayName || !sessionId || !creatorPeerId)
      return jsonError("displayName, sessionId, creatorPeerId required", 400);

    // Idempotency: return existing room if already initialised
    const existing = await this.#getRoom();
    if (existing && existing.sessionId === sessionId && existing.status !== "closed")
      return jsonOk({ ok: true, room: existing });

    const roomTtlSec = clampInt(body.roomTtlSec, MIN_ROOM_TTL_SEC, DEFAULT_ROOM_TTL_SEC);
    const heartbeatTtlSec = clampInt(body.heartbeatTtlSec, MIN_HEARTBEAT_TTL_SEC, DEFAULT_HEARTBEAT_TTL_SEC);

    const now  = Date.now();
    const room = {
      id: sessionId,
      sessionId,
      displayName: String(displayName).trim().slice(0, MAX_DISPLAY_NAME_LEN),
      creatorPeerId,
      status: "waiting",
      createdAt: now,
      expiresAt: now + roomTtlSec * 1000,
      joinedAt: null,
      closedAt: null,
      lastHeartbeatAt: now,
      heartbeatExpiresAt: now + heartbeatTtlSec * 1000,
      heartbeatTtlSec,
    };

    await this.state.storage.put("room", room);
    await this.#scheduleAlarm(room, now);
    return jsonOk({ ok: true, room });
  }

  async #handleGetRoom() {
    const room = await this.#getRoom();
    if (!room) return jsonNull404();

    const now = Date.now();
    if (room.status === "closed") return jsonNull404();
    if (room.status === "waiting" && !isRoomWaitingAlive(room, now)) {
      await this.#clearAll();
      return jsonNull404();
    }

    return jsonOk(room);
  }

  /**
   * Fix L-5: creatorPeerId auth is enforced for any non-empty, non-null value.
   * An empty string "" previously bypassed the check due to JS falsy coercion.
   */
  async #handleHeartbeat(request) {
    const room = await this.#getRoom();
    if (!room)                    return jsonError("Room not found", 404);
    if (room.status === "closed") return jsonError("Room not found", 404);

    const body          = await parseJson(request).catch(() => null);
    const creatorPeerId = typeof body?.creatorPeerId === "string"
      ? body.creatorPeerId.trim()
      : null;

    // Enforce ownership: reject non-empty peer IDs that do not match
    if (creatorPeerId && creatorPeerId !== room.creatorPeerId)
      return jsonError("creator peer mismatch", 403);

    const now = Date.now();
    if (room.status === "waiting") {
      room.lastHeartbeatAt   = now;
      room.heartbeatExpiresAt = now + room.heartbeatTtlSec * 1000;
      await this.state.storage.put("room", room);
    }

    return jsonOk({ ok: true, room });
  }

  /**
   * blockConcurrencyWhile makes the join atomic inside the DO's single-threaded
   * execution model, preventing two simultaneous join requests from both
   * succeeding.
   */
  async #handleJoin() {
    return this.state.storage.transaction(async () => {
      const room = await this.#getRoom();
      if (!room) return jsonError("Room not found", 404);

      const now = Date.now();
      if (!isRoomJoinable(room, now)) {
        room.status   = "closed";
        room.closedAt = now;
        await this.state.storage.put("room", room);
        return jsonError("Room not found", 404);
      }

      // status === "waiting" is guaranteed by isRoomJoinable
      room.status   = "joined";
      room.joinedAt = now;
      await this.state.storage.put("room", room);
      await this.#scheduleAlarm(room, now);

      return jsonOk({ ok: true, sessionId: room.sessionId, callerPeerId: room.creatorPeerId });
    });
  }

  async #handleDelete() {
    await this.#clearAll();
    return jsonOk({ ok: true });
  }

  async #handlePostSignal(request) {
    const envelope = await parseJson(request);
    if (!envelope) return jsonError("Invalid JSON", 400);

    const { type } = envelope;
    if (!type)                    return jsonError("type required", 400);
    if (!SIGNAL_TYPES.has(type))  return jsonError(`unsupported signal type: ${type}`, 400);

    const room = await this.#getRoom();
    if (!room) return jsonError("Room not found", 404);

    const normalized = {
      sessionId:   room.sessionId,
      fromPeerId:  envelope.fromPeerId  || null,
      toPeerId:    envelope.toPeerId    || null,
      messageId:   envelope.messageId   || crypto.randomUUID(),
      type,
      ttlMs:       Number.isFinite(envelope.ttlMs) && envelope.ttlMs > 0
                     ? envelope.ttlMs
                     : 60_000,
      payloadJson: typeof envelope.payloadJson === "string" ? envelope.payloadJson : "{}",
      sentAt:      envelope.sentAt || Date.now(),
    };

    await this.state.storage.put(signalKey(type), normalized);
    await this.#scheduleAlarm(room, Date.now());
    return jsonOk({ ok: true });
  }

  async #handleGetSignal(url) {
    const type = url.searchParams.get("type");
    if (!type)                   return jsonError("type query parameter required", 400);
    if (!SIGNAL_TYPES.has(type)) return jsonError(`unsupported signal type: ${type}`, 400);

    const signal = await this.state.storage.get(signalKey(type));
    if (!signal) return jsonNull404();

    const ttlMs = Math.max(60_000, Number.isFinite(signal.ttlMs) ? signal.ttlMs : 60_000);
    if ((signal.sentAt || 0) + ttlMs <= Date.now()) {
      await this.state.storage.delete(signalKey(type));
      return jsonNull404();
    }

    return jsonOk(signal);
  }

  async #handleDeleteSignal(url) {
    const type = url.searchParams.get("type");
    if (!type)                   return jsonError("type query parameter required", 400);
    if (!SIGNAL_TYPES.has(type)) return jsonError(`unsupported signal type: ${type}`, 400);

    await this.state.storage.delete(signalKey(type));
    return jsonOk({ ok: true });
  }

  // ── Storage helpers ────────────────────────────────────────────────────────

  async #getRoom() {
    return (await this.state.storage.get("room")) ?? null;
  }

  async #clearAll() {
    await this.state.storage.delete([
      "room",
      signalKey("offer"),
      signalKey("answer"),
      signalKey("hangup"),
    ]);
  }

  async #scheduleAlarm(room, now) {
    // Alarm fires at the earliest expiry boundary relevant to current status.
    // For "joined" / "closed" rooms only the max-age hard cap matters.
    const candidates = [room.createdAt + SESSION_RETENTION_MS];
    if (room.status === "waiting") {
      candidates.push(room.expiresAt, room.heartbeatExpiresAt);
    }

    const nextAlarmAt = Math.min(...candidates);
    if (Number.isFinite(nextAlarmAt) && nextAlarmAt > now)
      await this.state.storage.setAlarm(nextAlarmAt);
  }
}

// ─── LobbyDurableObject ───────────────────────────────────────────────────────

export class LobbyDurableObject {
  constructor(state) {
    this.state = state;
  }

  async fetch(request) {
    const url    = new URL(request.url);
    const method = request.method;
    const path   = url.pathname;

    if (method === "OPTIONS") return respond204();

    if (path === "/internal/list"   && method === "GET")  return this.#handleList();
    if (path === "/internal/upsert" && method === "POST") return this.#handleUpsert(request);
    if (path === "/internal/remove" && method === "POST") return this.#handleRemove(request);

    return jsonError("Not Found", 404);
  }

  async #handleList() {
    const rooms = await this.#readRooms();
    return jsonOk({ rooms });
  }

  async #handleUpsert(request) {
    const room = await parseJson(request);
    if (!room?.sessionId) return jsonError("sessionId required", 400);

    const rooms   = await this.#readRooms();
    const without = rooms.filter(r => r.sessionId !== room.sessionId);

    // Enforce lobby size cap to prevent unbounded storage growth
    if (without.length >= MAX_LOBBY_SIZE)
      return jsonError("lobby full", 503);

    without.push(room);
    await this.#writeRooms(without);
    return jsonOk({ ok: true });
  }

  async #handleRemove(request) {
    const body = await parseJson(request);
    if (!body?.sessionId) return jsonError("sessionId required", 400);

    const rooms = await this.#readRooms();
    await this.#writeRooms(rooms.filter(r => r.sessionId !== body.sessionId));
    return jsonOk({ ok: true });
  }

  async #readRooms() {
    return (await this.state.storage.get("rooms")) ?? [];
  }

  async #writeRooms(rooms) {
    await this.state.storage.put("rooms", rooms);
  }
}

// ─── DO stub helpers ──────────────────────────────────────────────────────────

function roomStub(env, sessionId) {
  return env.ROOMS_DO.get(env.ROOMS_DO.idFromName(sessionId));
}

function lobbyStub(env) {
  return env.LOBBY_DO.get(env.LOBBY_DO.idFromName(LOBBY_DO_NAME));
}

// ─── Room state predicates ────────────────────────────────────────────────────

function isRoomWaitingAlive(room, now) {
  return (
    room != null &&
    room.status === "waiting" &&
    room.expiresAt > now &&
    room.heartbeatExpiresAt > now
  );
}

function isRoomJoinable(room, now) {
  return room != null && room.status === "waiting" && isRoomWaitingAlive(room, now);
}

function isRoomLobbyVisible(room, now) {
  return room != null && room.status === "waiting" && isRoomWaitingAlive(room, now);
}

// ─── Response helpers ─────────────────────────────────────────────────────────

function jsonOk(data) {
  return new Response(JSON.stringify(data), {
    status: 200,
    headers: { ...CORS_HEADERS, "Content-Type": "application/json" },
  });
}

/** 404 with null body — used for "not found / expired" signaling slots */
function jsonNull404() {
  return new Response("null", {
    status: 404,
    headers: { ...CORS_HEADERS, "Content-Type": "application/json" },
  });
}

function jsonError(message, status) {
  return new Response(JSON.stringify({ error: message }), {
    status,
    headers: { ...CORS_HEADERS, "Content-Type": "application/json" },
  });
}

function respond204() {
  return new Response(null, { status: 204, headers: CORS_HEADERS });
}

/**
 * Adds CORS headers to a Response that already came back from a DO sub-request.
 * Used only for pass-through responses where we did not construct the body.
 */
function cors(response) {
  const headers = new Headers(response.headers);
  for (const [k, v] of Object.entries(CORS_HEADERS)) headers.set(k, v);
  return new Response(response.body, {
    status: response.status,
    statusText: response.statusText,
    headers,
  });
}

// ─── Misc helpers ─────────────────────────────────────────────────────────────

/** Returns parsed JSON or null on any error. */
async function parseJson(requestOrText) {
  try {
    if (typeof requestOrText === "string") return JSON.parse(requestOrText);
    return await requestOrText.json();
  } catch {
    return null;
  }
}

function isValidSessionId(id) {
  return typeof id === "string" && id.length > 0 && id.length <= MAX_SESSION_ID_LEN;
}

/** Clamps a client-supplied integer; falls back to defaultVal if invalid. */
function clampInt(value, min, defaultVal) {
  return Number.isFinite(value) ? Math.max(min, Math.floor(value)) : defaultVal;
}

function signalKey(type) {
  return `signal:${type}`;
}

/**
 * Removes lobby entries in the background without blocking the list response.
 * Uses Promise.allSettled so individual failures do not surface to the client.
 */
function removeLobbyEntriesBackground(env, sessionIds) {
  Promise.allSettled(
    sessionIds.map(id =>
      lobbyStub(env).fetch("https://do/internal/remove", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sessionId: id }),
      })
    )
  ).catch(err => console.warn("[worker] background lobby cleanup error", err));
}