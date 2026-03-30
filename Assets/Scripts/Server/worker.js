/**
 * Cloudflare Worker for WebRTC v2 signaling.
 *
 * Durable Object bindings:
 *   - ROOMS_DO: one RoomDurableObject per sessionId
 *   - LOBBY_DO: singleton LobbyDurableObject for waiting-room discovery
 */

const CORS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, POST, DELETE, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type",
};

const SIGNAL_TYPES = new Set(["offer", "answer", "hangup"]);
const DEFAULT_ROOM_TTL_SEC = 180;
const DEFAULT_HEARTBEAT_TTL_SEC = 30;
const MIN_ROOM_TTL_SEC = 60;
const MIN_HEARTBEAT_TTL_SEC = 15;
const SESSION_RETENTION_MS = 24 * 60 * 60 * 1000;
const LOBBY_ID = "global-lobby";

export default {
  async fetch(request, env) {
    try {
      const url = new URL(request.url);
      const method = request.method;
      const path = url.pathname;

      if (method === "OPTIONS") return respond204();

      if (path === "/api/rooms") {
        if (method === "GET") return withCors(await handleListRooms(env));
        if (method === "POST") return withCors(await handleCreateRoom(request, env));
      }

      const heartbeatMatch = path.match(/^\/api\/rooms\/([^/]+)\/heartbeat$/);
      if (heartbeatMatch && method === "POST")
        return withCors(await handleHeartbeatRoom(heartbeatMatch[1], request, env));

      const joinMatch = path.match(/^\/api\/rooms\/([^/]+)\/join$/);
      if (joinMatch && method === "POST")
        return withCors(await handleJoinRoom(joinMatch[1], env));

      const roomMatch = path.match(/^\/api\/rooms\/([^/]+)$/);
      if (roomMatch) {
        if (method === "GET") return withCors(await handleGetRoom(roomMatch[1], env));
        if (method === "DELETE") return withCors(await handleDeleteRoom(roomMatch[1], env));
      }

      const signalMatch = path.match(/^\/api\/signal\/([^/]+)$/);
      if (signalMatch) {
        const sessionId = signalMatch[1];
        if (method === "POST") return withCors(await handlePostSignal(sessionId, request, env));
        if (method === "GET") return withCors(await handleGetSignal(sessionId, url, env));
        if (method === "DELETE") return withCors(await handleDeleteSignal(sessionId, url, env));
      }

      return respondJson({ error: "Not Found" }, 404);
    } catch (error) {
      console.error("Unhandled worker error", error);
      return respondJson({ error: "Internal Server Error" }, 500);
    }
  },
};

export class RoomDurableObject {
  constructor(state) {
    this.state = state;
  }

  async fetch(request) {
    const url = new URL(request.url);
    const method = request.method;
    const path = url.pathname;

    if (method === "OPTIONS") return respond204();

    if (path === "/internal/init" && method === "POST")
      return this.handleInit(request);

    if (path === "/internal/room" && method === "GET")
      return this.handleGetRoom();

    if (path === "/internal/heartbeat" && method === "POST")
      return this.handleHeartbeat(request);

    if (path === "/internal/join" && method === "POST")
      return this.handleJoin();

    if (path === "/internal/delete" && method === "DELETE")
      return this.handleDelete();

    if (path === "/internal/signal") {
      if (method === "POST") return this.handlePostSignal(request);
      if (method === "GET") return this.handleGetSignal(url);
      if (method === "DELETE") return this.handleDeleteSignal(url);
    }

    return respondJson({ error: "Not Found" }, 404);
  }

  async alarm() {
    const room = await this.getRoom();
    if (!room) {
      await this.clearAll();
      return;
    }

    const now = Date.now();
    const maxAgeExceeded = room.createdAt + SESSION_RETENTION_MS <= now;
    const roomExpiredAndUnused =
      room.status === "waiting" && !isRoomWaitingAlive(room, now);

    if (maxAgeExceeded || roomExpiredAndUnused || room.status === "closed") {
      await this.clearAll();
      return;
    }

    await this.scheduleAlarm(room, now);
  }

  async handleInit(request) {
    let body;
    try {
      body = await request.json();
    } catch (error) {
      console.error("Invalid JSON in RoomDurableObject.handleInit", error);
      return respondJson({ error: "Invalid JSON" }, 400);
    }

    const { displayName, sessionId, creatorPeerId } = body || {};
    if (!displayName || !sessionId || !creatorPeerId)
      return respondJson({ error: "displayName, sessionId, creatorPeerId required" }, 400);

    const existingRoom = await this.getRoom();
    if (existingRoom && existingRoom.sessionId === sessionId && existingRoom.status !== "closed")
      return respondJson({ ok: true, room: existingRoom });

    const roomTtlSec = Math.max(
      MIN_ROOM_TTL_SEC,
      Number.isFinite(body.roomTtlSec) ? Math.floor(body.roomTtlSec) : DEFAULT_ROOM_TTL_SEC
    );
    const heartbeatTtlSec = Math.max(
      MIN_HEARTBEAT_TTL_SEC,
      Number.isFinite(body.heartbeatTtlSec) ? Math.floor(body.heartbeatTtlSec) : DEFAULT_HEARTBEAT_TTL_SEC
    );

    const now = Date.now();
    const room = {
      id: sessionId,
      sessionId,
      displayName: String(displayName).trim().slice(0, 64),
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
    await this.scheduleAlarm(room, now);
    return respondJson({ ok: true, room });
  }

  async handleGetRoom() {
    const room = await this.getRoom();
    if (!room) return respondJson(null, 404);

    const now = Date.now();
    if (room.status === "waiting" && !isRoomWaitingAlive(room, now)) {
      await this.clearAll();
      return respondJson(null, 404);
    }

    if (room.status === "closed") return respondJson(null, 404);
    return respondJson(room);
  }

  async handleHeartbeat(request) {
    const room = await this.getRoom();
    if (!room) return respondJson({ error: "Room not found" }, 404);

    let body = null;
    try {
      body = await request.json();
    } catch {
      body = null;
    }

    const creatorPeerId = typeof body?.creatorPeerId === "string"
      ? body.creatorPeerId.trim()
      : "";
    if (!creatorPeerId)
      return respondJson({ error: "creatorPeerId required" }, 400);
    if (creatorPeerId !== room.creatorPeerId)
      return respondJson({ error: "creator peer mismatch" }, 403);

    if (room.status === "closed")
      return respondJson({ error: "Room not found" }, 404);

    const now = Date.now();
    if (room.status === "waiting") {
      room.lastHeartbeatAt = now;
      room.heartbeatExpiresAt = now + room.heartbeatTtlSec * 1000;
      await this.state.storage.put("room", room);
    }

    return respondJson({ ok: true, room });
  }

  async handleJoin() {
    const room = await this.getRoom();
    if (!room) return respondJson({ error: "Room not found" }, 404);

    const now = Date.now();
    if (!isRoomJoinable(room, now)) {
      room.status = "closed";
      room.closedAt = now;
      await this.state.storage.put("room", room);
      return respondJson({ error: "Room not found" }, 404);
    }

    if (room.status !== "waiting")
      return respondJson({ error: `Room is ${room.status}, cannot join` }, 409);

    room.status = "joined";
    room.joinedAt = now;
    await this.state.storage.put("room", room);
    await this.scheduleAlarm(room, now);

    return respondJson({
      ok: true,
      sessionId: room.sessionId,
      callerPeerId: room.creatorPeerId,
    });
  }

  async handleDelete() {
    await this.clearAll();
    return respondJson({ ok: true });
  }

  async handlePostSignal(request) {
    let envelope;
    try {
      envelope = await request.json();
    } catch (error) {
      console.error("Invalid JSON in RoomDurableObject.handlePostSignal", error);
      return respondJson({ error: "Invalid JSON" }, 400);
    }

    const type = envelope?.type;
    if (!type) return respondJson({ error: "type required" }, 400);
    if (!SIGNAL_TYPES.has(type))
      return respondJson({ error: `unsupported signal type: ${type}` }, 400);

    const room = await this.getRoom();
    if (!room) return respondJson({ error: "Room not found" }, 404);

    const normalizedEnvelope = {
      sessionId: room.sessionId,
      fromPeerId: envelope.fromPeerId || null,
      toPeerId: envelope.toPeerId || null,
      messageId: envelope.messageId || crypto.randomUUID(),
      type,
      ttlMs: envelope.ttlMs || 60_000,
      payloadJson: typeof envelope.payloadJson === "string" ? envelope.payloadJson : "{}",
      sentAt: envelope.sentAt || Date.now(),
    };

    await this.state.storage.put(this.signalKey(type), normalizedEnvelope);
    await this.scheduleAlarm(room, Date.now());
    return respondJson({ ok: true });
  }

  async handleGetSignal(url) {
    const type = url.searchParams.get("type");
    if (!type) return respondJson({ error: "type query parameter required" }, 400);
    if (!SIGNAL_TYPES.has(type))
      return respondJson({ error: `unsupported signal type: ${type}` }, 400);

    const signal = await this.state.storage.get(this.signalKey(type));
    if (!signal) return respondJson(null, 404);

    const ttlMs = Math.max(60_000, signal.ttlMs || 60_000);
    if ((signal.sentAt || 0) + ttlMs <= Date.now()) {
      await this.state.storage.delete(this.signalKey(type));
      return respondJson(null, 404);
    }

    return respondJson(signal);
  }

  async handleDeleteSignal(url) {
    const type = url.searchParams.get("type");
    if (!type) return respondJson({ error: "type query parameter required" }, 400);
    if (!SIGNAL_TYPES.has(type))
      return respondJson({ error: `unsupported signal type: ${type}` }, 400);

    await this.state.storage.delete(this.signalKey(type));
    return respondJson({ ok: true });
  }

  async getRoom() {
    return (await this.state.storage.get("room")) || null;
  }

  signalKey(type) {
    return `signal:${type}`;
  }

  async clearAll() {
    await this.state.storage.delete([
      "room",
      this.signalKey("offer"),
      this.signalKey("answer"),
      this.signalKey("hangup"),
    ]);
  }

  async scheduleAlarm(room, now) {
    const nextAlarmAt = Math.min(
      room.createdAt + SESSION_RETENTION_MS,
      room.status === "waiting" ? room.expiresAt : room.createdAt + SESSION_RETENTION_MS,
      room.status === "waiting" ? room.heartbeatExpiresAt : room.createdAt + SESSION_RETENTION_MS
    );

    if (Number.isFinite(nextAlarmAt) && nextAlarmAt > now)
      await this.state.storage.setAlarm(nextAlarmAt);
  }
}

export class LobbyDurableObject {
  constructor(state) {
    this.state = state;
  }

  async fetch(request) {
    const url = new URL(request.url);
    const method = request.method;
    const path = url.pathname;

    if (method === "OPTIONS") return respond204();

    if (path === "/internal/list" && method === "GET")
      return this.handleList();

    if (path === "/internal/upsert" && method === "POST")
      return this.handleUpsert(request);

    if (path === "/internal/remove" && method === "POST")
      return this.handleRemove(request);

    return respondJson({ error: "Not Found" }, 404);
  }

  async handleList() {
    const rooms = await this.readRooms();
    return respondJson({ rooms });
  }

  async handleUpsert(request) {
    let room;
    try {
      room = await request.json();
    } catch (error) {
      console.error("Invalid JSON in LobbyDurableObject.handleUpsert", error);
      return respondJson({ error: "Invalid JSON" }, 400);
    }

    if (!room?.sessionId)
      return respondJson({ error: "sessionId required" }, 400);

    const rooms = await this.readRooms();
    const filtered = rooms.filter((entry) => entry.sessionId !== room.sessionId);
    filtered.push(room);
    await this.writeRooms(filtered);
    return respondJson({ ok: true });
  }

  async handleRemove(request) {
    let body;
    try {
      body = await request.json();
    } catch (error) {
      console.error("Invalid JSON in LobbyDurableObject.handleRemove", error);
      return respondJson({ error: "Invalid JSON" }, 400);
    }

    const sessionId = body?.sessionId;
    if (!sessionId) return respondJson({ error: "sessionId required" }, 400);

    const rooms = await this.readRooms();
    const filtered = rooms.filter((entry) => entry.sessionId !== sessionId);
    await this.writeRooms(filtered);
    return respondJson({ ok: true });
  }

  async readRooms() {
    return (await this.state.storage.get("rooms")) || [];
  }

  async writeRooms(rooms) {
    await this.state.storage.put("rooms", rooms);
  }
}

async function handleListRooms(env) {
  const response = await lobbyStub(env).fetch("https://lobby/internal/list");
  if (!response.ok) return response;

  const payload = await response.json();
  const lobbyRooms = Array.isArray(payload?.rooms) ? payload.rooms : [];
  if (lobbyRooms.length === 0)
    return respondJson({ rooms: [] });

  const now = Date.now();
  const visibleRooms = [];
  const staleSessionIds = [];
  const settled = await Promise.allSettled(
    lobbyRooms
      .filter((room) => !!room?.sessionId)
      .map(async (lobbyRoom) => {
        const roomResponse = await roomStub(env, lobbyRoom.sessionId).fetch("https://room/internal/room", {
          method: "GET",
        });

        return {
          sessionId: lobbyRoom.sessionId,
          response: roomResponse,
          room: roomResponse.ok ? await roomResponse.json() : null,
        };
      })
  );

  for (const result of settled) {
    if (result.status !== "fulfilled")
      continue;

    const { sessionId, response: roomResponse, room } = result.value;
    if (!roomResponse.ok || !isRoomLobbyVisible(room, now)) {
      staleSessionIds.push(sessionId);
      continue;
    }

    visibleRooms.push(room);
  }

  if (staleSessionIds.length > 0) {
    await Promise.allSettled(
      staleSessionIds.map((sessionId) =>
        lobbyStub(env).fetch("https://lobby/internal/remove", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ sessionId }),
        })
      )
    );
  }

  return respondJson({ rooms: visibleRooms });
}

async function handleCreateRoom(request, env) {
  let body;
  try {
    body = await request.json();
  } catch (error) {
    console.error("Invalid JSON in create room", error);
    return respondJson({ error: "Invalid JSON" }, 400);
  }

  const { displayName, sessionId, creatorPeerId } = body || {};
  if (!displayName || !sessionId || !creatorPeerId)
    return respondJson({ error: "displayName, sessionId, creatorPeerId required" }, 400);

  const initResponse = await roomStub(env, sessionId).fetch("https://room/internal/init", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });

  if (!initResponse.ok) return initResponse;

  const payload = await initResponse.json();
  const room = payload?.room;
  if (!room) return respondJson({ error: "Room initialization failed" }, 500);

  await lobbyStub(env).fetch("https://lobby/internal/upsert", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(room),
  });

  return respondJson({ ok: true, room });
}

async function handleGetRoom(sessionId, env) {
  if (!sessionId) return respondJson({ error: "sessionId required" }, 400);
  return roomStub(env, sessionId).fetch("https://room/internal/room", { method: "GET" });
}

async function handleHeartbeatRoom(sessionId, request, env) {
  if (!sessionId) return respondJson({ error: "sessionId required" }, 400);

  return roomStub(env, sessionId).fetch("https://room/internal/heartbeat", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: await request.text(),
  });
}

async function handleJoinRoom(sessionId, env) {
  if (!sessionId) return respondJson({ error: "sessionId required" }, 400);

  const response = await roomStub(env, sessionId).fetch("https://room/internal/join", {
    method: "POST",
  });

  if (response.ok) {
    await lobbyStub(env).fetch("https://lobby/internal/remove", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sessionId }),
    });
  }

  return response;
}

async function handleDeleteRoom(sessionId, env) {
  if (!sessionId) return respondJson({ error: "sessionId required" }, 400);

  await roomStub(env, sessionId).fetch("https://room/internal/delete", {
    method: "DELETE",
  });

  await lobbyStub(env).fetch("https://lobby/internal/remove", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ sessionId }),
  });

  return respondJson({ ok: true });
}

async function handlePostSignal(sessionId, request, env) {
  return roomStub(env, sessionId).fetch("https://room/internal/signal", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: await request.text(),
  });
}

async function handleGetSignal(sessionId, url, env) {
  const type = url.searchParams.get("type");
  if (!type) return respondJson({ error: "type query parameter required" }, 400);

  return roomStub(env, sessionId).fetch(
    `https://room/internal/signal?type=${encodeURIComponent(type)}`,
    { method: "GET" }
  );
}

async function handleDeleteSignal(sessionId, url, env) {
  const type = url.searchParams.get("type");
  if (!type) return respondJson({ error: "type query parameter required" }, 400);

  return roomStub(env, sessionId).fetch(
    `https://room/internal/signal?type=${encodeURIComponent(type)}`,
    { method: "DELETE" }
  );
}

function roomStub(env, sessionId) {
  const id = env.ROOMS_DO.idFromName(sessionId);
  return env.ROOMS_DO.get(id);
}

function lobbyStub(env) {
  const id = env.LOBBY_DO.idFromName(LOBBY_ID);
  return env.LOBBY_DO.get(id);
}

function isRoomWaitingAlive(room, now) {
  return room &&
    room.status === "waiting" &&
    room.expiresAt > now &&
    room.heartbeatExpiresAt > now;
}

function isRoomJoinable(room, now) {
  return room && room.status === "waiting" && isRoomWaitingAlive(room, now);
}

function isRoomLobbyVisible(room, now) {
  return room && room.status === "waiting" && isRoomWaitingAlive(room, now);
}

function respondJson(data, status = 200) {
  return new Response(JSON.stringify(data), {
    status,
    headers: { ...CORS, "Content-Type": "application/json" },
  });
}

function respond204() {
  return new Response(null, { status: 204, headers: CORS });
}

function withCors(response) {
  const headers = new Headers(response.headers);
  for (const [key, value] of Object.entries(CORS))
    headers.set(key, value);

  return new Response(response.body, {
    status: response.status,
    statusText: response.statusText,
    headers,
  });
}
