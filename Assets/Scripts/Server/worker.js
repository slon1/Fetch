const CORS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, POST, DELETE, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type",
};

const SIGNAL_TYPES = new Set(["offer", "answer", "hangup"]);
const SWITCHBOARD_ID = "switchboard";
const CALL_RETENTION_MS = 24 * 60 * 60 * 1000;
const BOOTH_NUMBER_LENGTH = 12;
const RING_TIMEOUT_MS = 25 * 1000;

const LINE_STATES = {
  IDLE: "idle",
  DIALING: "dialing",
  RINGING_OUTGOING: "ringing_outgoing",
  RINGING_INCOMING: "ringing_incoming",
  CONNECTING: "connecting",
  IN_CALL: "in_call",
};

const SOCKET_EVENT_TYPES = {
  LINE_SNAPSHOT: "line_snapshot",
  INCOMING_CALL: "incoming_call",
  OUTGOING_RINGING: "outgoing_ringing",
  CALL_ACCEPTED: "call_accepted",
  CALL_REJECTED: "call_rejected",
  REMOTE_HANGUP: "remote_hangup",
  BUSY: "busy",
  OFFLINE: "offline",
  LINE_RESET: "line_reset",
};

export default {
  async fetch(request, env) {
    try {
      const url = new URL(request.url);
      const method = request.method;
      const path = url.pathname;

      if (method === "OPTIONS") return respond204();

      if (path === "/api/booths/register" && method === "POST")
        return withCors(await handleRegisterBooth(request, env));

      const boothEventsMatch = path.match(/^\/api\/booths\/([^/]+)\/events$/);
      if (boothEventsMatch && method === "GET")
        return handleBoothEvents(boothEventsMatch[1], request, env);

      if (path === "/api/dial" && method === "POST")
        return withCors(await handleDial(request, env));

      const callActionMatch = path.match(/^\/api\/calls\/([^/]+)\/(accept|reject|hangup|connected)$/);
      if (callActionMatch && method === "POST")
        return withCors(await handleCallAction(callActionMatch[1], callActionMatch[2], request, env));

      const signalMatch = path.match(/^\/api\/signal\/([^/]+)$/);
      if (signalMatch) {
        const callId = signalMatch[1];
        if (method === "POST") return withCors(await handlePostSignal(callId, request, env));
        if (method === "GET") return withCors(await handleGetSignal(callId, url, env));
        if (method === "DELETE") return withCors(await handleDeleteSignal(callId, url, env));
      }

      if (path === "/api/rooms" && method === "GET")
        return withCors(respondJson({ rooms: [] }));

      return respondJson({ error: "Not Found" }, 404);
    } catch (error) {
      console.error("Unhandled worker error", error);
      return respondJson({ error: "Internal Server Error" }, 500);
    }
  },
};

export class BoothDurableObject {
  constructor(state, env) {
    this.state = state;
    this.env = env;
    this.webSockets = new Set();
  }

  async fetch(request) {
    const url = new URL(request.url);
    const method = request.method;
    const path = url.pathname;

    if (method === "OPTIONS") return respond204();

    if (path === "/internal/register" && method === "POST")
      return this.handleRegister(request);

    if (path === "/internal/status" && method === "GET")
      return this.handleStatus();

    if (path === "/internal/events" && method === "GET")
      return this.handleEvents(request);

    if (path === "/internal/apply" && method === "POST")
      return this.handleApply(request);

    if (path === "/internal/reset" && method === "POST")
      return this.handleReset(request);

    return respondJson({ error: "Not Found" }, 404);
  }

  webSocketClose(socket) {
    this.webSockets.delete(socket);
  }

  webSocketError(socket) {
    this.webSockets.delete(socket);
  }

  async handleRegister(request) {
    const body = await readJson(request);
    const boothNumber = normalizeNumber(body?.boothNumber);
    const ownerClientId = normalizeString(body?.ownerClientId);
    if (!boothNumber || !ownerClientId)
      return respondJson({ ok: false, error: "boothNumber and ownerClientId required" }, 400);

    const existing = await this.getRegistration();
    if (existing && existing.ownerClientId !== ownerClientId)
      return respondJson({ ok: false, error: "number_conflict", boothNumber }, 409);

    const now = Date.now();
    const registration = existing || {
      boothNumber,
      ownerClientId,
      createdAt: now,
      lastSeenAt: now,
    };
    registration.lastSeenAt = now;

    await this.state.storage.put("registration", registration);
    if (!(await this.getLine()))
      await this.state.storage.put("line", defaultLine());

    return respondJson({ ok: true, boothNumber, ownerClientId });
  }

  async handleStatus() {
    const registration = await this.getRegistration();
    const line = await this.getConsistentLine();
    const online = this.getConnectedSockets().length > 0;

    return respondJson({
      registered: !!registration,
      boothNumber: registration?.boothNumber || null,
      ownerClientId: registration?.ownerClientId || null,
      online,
      lineState: line.lineState,
      currentCallId: line.currentCallId,
      peerNumber: line.peerNumber,
      direction: line.direction,
      callerNumber: line.callerNumber,
      calleeNumber: line.calleeNumber,
      callerClientId: line.callerClientId,
      createdAt: registration?.createdAt || null,
      lastSeenAt: registration?.lastSeenAt || null,
    });
  }

  async handleEvents(request) {
    const registration = await this.getRegistration();
    if (!registration)
      return new Response("Booth not registered", { status: 404 });

    const url = new URL(request.url);
    const clientId = normalizeString(url.searchParams.get("clientId"));
    if (!clientId || clientId !== registration.ownerClientId)
      return new Response("Forbidden", { status: 403 });

    const upgrade = request.headers.get("Upgrade");
    if (!upgrade || upgrade.toLowerCase() !== "websocket")
      return new Response("Expected websocket", { status: 426 });

    registration.lastSeenAt = Date.now();
    await this.state.storage.put("registration", registration);

    const [client, server] = Object.values(new WebSocketPair());
    if (typeof this.state.acceptWebSocket === "function") {
      this.state.acceptWebSocket(server);
    } else {
      server.accept();
      this.webSockets.add(server);
      server.addEventListener("close", () => this.webSockets.delete(server));
      server.addEventListener("error", () => this.webSockets.delete(server));
    }

    for (const socket of this.getConnectedSockets()) {
      if (socket === server) continue;
      try { socket.close(1000, "superseded"); } catch {}
      this.webSockets.delete(socket);
    }

    await this.sendLineSnapshot(server, registration.boothNumber);
    return new Response(null, { status: 101, webSocket: client });
  }

  async handleApply(request) {
    const body = await readJson(request);
    const line = {
      lineState: normalizeLineState(body?.lineState),
      currentCallId: normalizeString(body?.currentCallId),
      peerNumber: normalizeNumber(body?.peerNumber),
      direction: normalizeString(body?.direction),
      callerNumber: normalizeNumber(body?.callerNumber),
      calleeNumber: normalizeNumber(body?.calleeNumber),
      callerClientId: normalizeString(body?.callerClientId),
      updatedAt: Date.now(),
    };
    await this.state.storage.put("line", line);

    const registration = await this.getRegistration();
    const boothNumber = registration?.boothNumber || normalizeNumber(body?.boothNumber);
    const eventType = normalizeString(body?.eventType) || SOCKET_EVENT_TYPES.LINE_SNAPSHOT;
    const reason = normalizeString(body?.reason);
    await this.broadcast(lineEvent(eventType, boothNumber, line, reason));
    return respondJson({ ok: true });
  }

  async handleReset(request) {
    const body = await readJson(request);
    const registration = await this.getRegistration();
    const boothNumber = registration?.boothNumber || normalizeNumber(body?.boothNumber);
    await this.state.storage.put("line", defaultLine());
    await this.broadcast(lineEvent(SOCKET_EVENT_TYPES.LINE_RESET, boothNumber, defaultLine(), normalizeString(body?.reason)));
    return respondJson({ ok: true });
  }

  async getRegistration() {
    return (await this.state.storage.get("registration")) || null;
  }

  async getLine() {
    return (await this.state.storage.get("line")) || null;
  }

  async getConsistentLine() {
    const line = (await this.getLine()) || defaultLine();
    if (!line.currentCallId)
      return line;

    const call = await getCall(this.env, line.currentCallId);
    if (call && call.status !== "closed")
      return line;

    const reset = defaultLine();
    await this.state.storage.put("line", reset);
    return reset;
  }

  getConnectedSockets() {
    if (typeof this.state.getWebSockets === "function")
      return this.state.getWebSockets();
    return Array.from(this.webSockets);
  }

  async sendLineSnapshot(socket, boothNumber) {
    const line = await this.getConsistentLine();
    socket.send(JSON.stringify(lineEvent(SOCKET_EVENT_TYPES.LINE_SNAPSHOT, boothNumber, line, null)));
  }

  async broadcast(payload) {
    const message = JSON.stringify(payload);
    const sockets = this.getConnectedSockets();
    await Promise.allSettled(sockets.map(async (socket) => {
      try {
        socket.send(message);
      } catch {
        this.webSockets.delete(socket);
        try { socket.close(1011, "broadcast-failed"); } catch {}
      }
    }));
  }
}

export class RoomDurableObject {
  constructor(state, env) {
    this.state = state;
    this.env = env;
  }

  async fetch(request) {
    const url = new URL(request.url);
    const method = request.method;
    const path = url.pathname;

    if (method === "OPTIONS") return respond204();

    if (path === "/internal/init-call" && method === "POST")
      return this.handleInitCall(request);
    if (path === "/internal/call" && method === "GET")
      return this.handleGetCall();
    if (path === "/internal/accept" && method === "POST")
      return this.handleAccept();
    if (path === "/internal/close" && method === "POST")
      return this.handleClose(request);
    if (path === "/internal/signal") {
      if (method === "POST") return this.handlePostSignal(request);
      if (method === "GET") return this.handleGetSignal(url);
      if (method === "DELETE") return this.handleDeleteSignal(url);
    }

    return respondJson({ error: "Not Found" }, 404);
  }

  async alarm() {
    const call = await this.getCall();
    if (!call) {
      await this.clearAll();
      return;
    }

    const now = Date.now();
    if (call.status === "ringing" && call.ringExpiresAt <= now) {
      await this.resetBooths("ring-timeout");
      await this.markClosed("ring-timeout");
      return;
    }

    if (call.closedAt && call.closedAt + CALL_RETENTION_MS <= now) {
      await this.clearAll();
      return;
    }

    await this.scheduleAlarm(call, now);
  }

  async handleInitCall(request) {
    const body = await readJson(request);
    const callId = normalizeString(body?.callId);
    const callerNumber = normalizeNumber(body?.callerNumber);
    const calleeNumber = normalizeNumber(body?.calleeNumber);
    const callerClientId = normalizeString(body?.callerClientId);
    if (!callId || !callerNumber || !calleeNumber || !callerClientId)
      return respondJson({ ok: false, error: "invalid_call_init" }, 400);

    const existing = await this.getCall();
    if (existing && existing.callId === callId && existing.status !== "closed")
      return respondJson({ ok: true, call: existing });

    const now = Date.now();
    const call = {
      callId,
      callerNumber,
      calleeNumber,
      callerClientId,
      status: "ringing",
      createdAt: now,
      acceptedAt: null,
      closedAt: null,
      ringExpiresAt: now + RING_TIMEOUT_MS,
      closeReason: null,
    };
    await this.state.storage.put("call", call);
    await this.scheduleAlarm(call, now);
    return respondJson({ ok: true, call });
  }

  async handleGetCall() {
    const call = await this.getCall();
    if (!call) return respondJson(null, 404);
    return respondJson(call);
  }

  async handleAccept() {
    const call = await this.getCall();
    if (!call || call.status === "closed")
      return respondJson({ ok: false, error: "call_not_found" }, 404);

    call.status = "connecting";
    call.acceptedAt = Date.now();
    await this.state.storage.put("call", call);
    await this.scheduleAlarm(call, Date.now());
    return respondJson({ ok: true, call });
  }

  async handleClose(request) {
    const body = await readJson(request);
    const reason = normalizeString(body?.reason) || "closed";
    await this.markClosed(reason);
    return respondJson({ ok: true });
  }

  async handlePostSignal(request) {
    const envelope = await readJson(request);
    const type = envelope?.type;
    if (!type || !SIGNAL_TYPES.has(type))
      return respondJson({ error: "unsupported signal type" }, 400);

    const call = await this.getCall();
    if (!call) return respondJson({ error: "Call not found" }, 404);

    const normalizedEnvelope = {
      sessionId: call.callId,
      fromPeerId: envelope.fromPeerId || null,
      toPeerId: envelope.toPeerId || null,
      messageId: envelope.messageId || crypto.randomUUID(),
      type,
      ttlMs: envelope.ttlMs || 60_000,
      payloadJson: typeof envelope.payloadJson === "string" ? envelope.payloadJson : "{}",
      sentAt: envelope.sentAt || Date.now(),
    };

    await this.state.storage.put(signalKey(type), normalizedEnvelope);
    await this.scheduleAlarm(call, Date.now());
    return respondJson({ ok: true });
  }

  async handleGetSignal(url) {
    const type = url.searchParams.get("type");
    if (!type || !SIGNAL_TYPES.has(type))
      return respondJson({ error: "type query parameter required" }, 400);

    const signal = await this.state.storage.get(signalKey(type));
    if (!signal) return respondJson(null, 404);

    const ttlMs = Math.max(60_000, signal.ttlMs || 60_000);
    if ((signal.sentAt || 0) + ttlMs <= Date.now()) {
      await this.state.storage.delete(signalKey(type));
      return respondJson(null, 404);
    }

    return respondJson(signal);
  }

  async handleDeleteSignal(url) {
    const type = url.searchParams.get("type");
    if (!type || !SIGNAL_TYPES.has(type))
      return respondJson({ error: "type query parameter required" }, 400);

    await this.state.storage.delete(signalKey(type));
    return respondJson({ ok: true });
  }

  async getCall() {
    return (await this.state.storage.get("call")) || null;
  }

  async markClosed(reason) {
    const call = await this.getCall();
    if (!call) return;
    call.status = "closed";
    call.closedAt = Date.now();
    call.closeReason = reason;
    await this.state.storage.put("call", call);
    await this.scheduleAlarm(call, Date.now());
  }

  async resetBooths(reason) {
    const call = await this.getCall();
    if (!call) return;
    await Promise.allSettled([
      boothStub(this.env, call.callerNumber).fetch("https://booth/internal/reset", jsonRequest({ boothNumber: call.callerNumber, reason })),
      boothStub(this.env, call.calleeNumber).fetch("https://booth/internal/reset", jsonRequest({ boothNumber: call.calleeNumber, reason })),
    ]);
  }

  async clearAll() {
    await this.state.storage.delete([
      "call",
      signalKey("offer"),
      signalKey("answer"),
      signalKey("hangup"),
    ]);
  }

  async scheduleAlarm(call, now) {
    const nextAlarmAt = call.status === "ringing"
      ? Math.min(call.ringExpiresAt, call.createdAt + CALL_RETENTION_MS)
      : (call.closedAt ? call.closedAt + CALL_RETENTION_MS : call.createdAt + CALL_RETENTION_MS);

    if (Number.isFinite(nextAlarmAt) && nextAlarmAt > now)
      await this.state.storage.setAlarm(nextAlarmAt);
  }
}

export class LobbyDurableObject {
  constructor(state, env) {
    this.state = state;
    this.env = env;
  }

  async fetch(request) {
    const url = new URL(request.url);
    const method = request.method;
    const path = url.pathname;

    if (method === "OPTIONS") return respond204();

    if (path === "/internal/dial" && method === "POST")
      return this.handleDial(request);
    if (path === "/internal/accept" && method === "POST")
      return this.handleAccept(request);
    if (path === "/internal/reject" && method === "POST")
      return this.handleReject(request);
    if (path === "/internal/connected" && method === "POST")
      return this.handleConnected(request);
    if (path === "/internal/hangup" && method === "POST")
      return this.handleHangup(request);

    return respondJson({ error: "Not Found" }, 404);
  }

  async handleDial(request) {
    const body = await readJson(request);
    const callerNumber = normalizeNumber(body?.callerNumber);
    const callerClientId = normalizeString(body?.callerClientId);
    const targetNumber = normalizeNumber(body?.targetNumber);
    if (!callerNumber || !callerClientId || !targetNumber)
      return respondJson({ ok: false, error: "invalid_dial" }, 400);

    const callerStatus = await getBoothStatus(this.env, callerNumber);
    const targetStatus = await getBoothStatus(this.env, targetNumber);

    if (!callerStatus.registered || callerStatus.ownerClientId !== callerClientId)
      return respondJson({ ok: false, error: "caller_not_registered" }, 403);

    if (!targetStatus.registered)
      return respondJson({ ok: true, outcome: "not_registered" });

    if (!targetStatus.online)
      return respondJson({ ok: true, outcome: "offline" });

    if (callerStatus.lineState !== LINE_STATES.IDLE) {
      if (callerStatus.currentCallId && callerStatus.peerNumber === targetNumber)
        return respondJson(buildRingingResponse(callerStatus));
      return respondJson({ ok: true, outcome: "busy" });
    }

    if (targetStatus.lineState !== LINE_STATES.IDLE) {
      if (targetStatus.currentCallId && targetStatus.peerNumber === callerNumber)
        return respondJson(buildRingingResponse(targetStatus));
      return respondJson({ ok: true, outcome: "busy" });
    }

    const callId = crypto.randomUUID();
    const initResponse = await roomStub(this.env, callId).fetch("https://call/internal/init-call", jsonRequest({
      callId,
      callerNumber,
      calleeNumber: targetNumber,
      callerClientId,
    }));
    if (!initResponse.ok)
      return respondJson({ ok: false, error: "call_init_failed" }, 500);

    await Promise.all([
      boothStub(this.env, callerNumber).fetch("https://booth/internal/apply", jsonRequest({
        boothNumber: callerNumber,
        lineState: LINE_STATES.RINGING_OUTGOING,
        currentCallId: callId,
        peerNumber: targetNumber,
        direction: "outgoing",
        callerNumber,
        calleeNumber: targetNumber,
        callerClientId,
        eventType: SOCKET_EVENT_TYPES.OUTGOING_RINGING,
      })),
      boothStub(this.env, targetNumber).fetch("https://booth/internal/apply", jsonRequest({
        boothNumber: targetNumber,
        lineState: LINE_STATES.RINGING_INCOMING,
        currentCallId: callId,
        peerNumber: callerNumber,
        direction: "incoming",
        callerNumber,
        calleeNumber: targetNumber,
        callerClientId,
        eventType: SOCKET_EVENT_TYPES.INCOMING_CALL,
      })),
    ]);

    return respondJson({
      ok: true,
      outcome: "ringing",
      callId,
      callerNumber,
      calleeNumber: targetNumber,
      callerClientId,
    });
  }

  async handleAccept(request) {
    const body = await readJson(request);
    const boothNumber = normalizeNumber(body?.boothNumber);
    const clientId = normalizeString(body?.clientId);
    const callId = normalizeCallIdFromRequest(request);
    if (!boothNumber || !clientId || !callId)
      return respondJson({ ok: false, error: "invalid_accept" }, 400);

    const boothStatus = await getBoothStatus(this.env, boothNumber);
    if (!boothStatus.registered || boothStatus.ownerClientId !== clientId)
      return respondJson({ ok: false, error: "booth_not_registered" }, 403);
    if (boothStatus.currentCallId !== callId)
      return respondJson({ ok: false, error: "call_mismatch" }, 409);

    const call = await getCall(this.env, callId);
    if (!call || call.status === "closed")
      return respondJson({ ok: false, error: "call_not_found" }, 404);

    await roomStub(this.env, callId).fetch("https://call/internal/accept", { method: "POST" });
    await Promise.all([
      boothStub(this.env, call.callerNumber).fetch("https://booth/internal/apply", jsonRequest({
        boothNumber: call.callerNumber,
        lineState: LINE_STATES.CONNECTING,
        currentCallId: callId,
        peerNumber: call.calleeNumber,
        direction: "outgoing",
        callerNumber: call.callerNumber,
        calleeNumber: call.calleeNumber,
        callerClientId: call.callerClientId,
        eventType: SOCKET_EVENT_TYPES.CALL_ACCEPTED,
      })),
      boothStub(this.env, call.calleeNumber).fetch("https://booth/internal/apply", jsonRequest({
        boothNumber: call.calleeNumber,
        lineState: LINE_STATES.CONNECTING,
        currentCallId: callId,
        peerNumber: call.callerNumber,
        direction: "incoming",
        callerNumber: call.callerNumber,
        calleeNumber: call.calleeNumber,
        callerClientId: call.callerClientId,
        eventType: SOCKET_EVENT_TYPES.LINE_SNAPSHOT,
      })),
    ]);

    return respondJson({
      ok: true,
      callId,
      callerNumber: call.callerNumber,
      calleeNumber: call.calleeNumber,
      callerClientId: call.callerClientId,
    });
  }

  async handleConnected(request) {
    const body = await readJson(request);
    const boothNumber = normalizeNumber(body?.boothNumber);
    const clientId = normalizeString(body?.clientId);
    const callId = normalizeCallIdFromRequest(request);
    if (!boothNumber || !clientId || !callId)
      return respondJson({ ok: false, error: "invalid_connected" }, 400);

    const boothStatus = await getBoothStatus(this.env, boothNumber);
    if (!boothStatus.registered || boothStatus.ownerClientId !== clientId)
      return respondJson({ ok: false, error: "booth_not_registered" }, 403);
    if (boothStatus.currentCallId !== callId)
      return respondJson({ ok: false, error: "call_mismatch" }, 409);

    const call = await getCall(this.env, callId);
    if (!call || call.status === "closed")
      return respondJson({ ok: false, error: "call_not_found" }, 404);

    await Promise.allSettled([
      boothStub(this.env, call.callerNumber).fetch("https://booth/internal/apply", jsonRequest({
        boothNumber: call.callerNumber,
        lineState: LINE_STATES.IN_CALL,
        currentCallId: callId,
        peerNumber: call.calleeNumber,
        direction: "outgoing",
        callerNumber: call.callerNumber,
        calleeNumber: call.calleeNumber,
        callerClientId: call.callerClientId,
        eventType: SOCKET_EVENT_TYPES.LINE_SNAPSHOT,
      })),
      boothStub(this.env, call.calleeNumber).fetch("https://booth/internal/apply", jsonRequest({
        boothNumber: call.calleeNumber,
        lineState: LINE_STATES.IN_CALL,
        currentCallId: callId,
        peerNumber: call.callerNumber,
        direction: "incoming",
        callerNumber: call.callerNumber,
        calleeNumber: call.calleeNumber,
        callerClientId: call.callerClientId,
        eventType: SOCKET_EVENT_TYPES.LINE_SNAPSHOT,
      })),
    ]);

    return respondJson({
      ok: true,
      callId,
      callerNumber: call.callerNumber,
      calleeNumber: call.calleeNumber,
      callerClientId: call.callerClientId,
    });
  }

  async handleReject(request) {
    return this.handleTerminate(request, SOCKET_EVENT_TYPES.CALL_REJECTED, "rejected");
  }

  async handleHangup(request) {
    return this.handleTerminate(request, SOCKET_EVENT_TYPES.REMOTE_HANGUP, "hangup");
  }

  async handleTerminate(request, remoteEventType, reason) {
    const body = await readJson(request);
    const boothNumber = normalizeNumber(body?.boothNumber);
    const clientId = normalizeString(body?.clientId);
    const callId = normalizeCallIdFromRequest(request);
    if (!boothNumber || !clientId || !callId)
      return respondJson({ ok: false, error: "invalid_call_action" }, 400);

    const boothStatus = await getBoothStatus(this.env, boothNumber);
    if (!boothStatus.registered || boothStatus.ownerClientId !== clientId)
      return respondJson({ ok: false, error: "booth_not_registered" }, 403);

    const call = await getCall(this.env, callId);
    if (!call) {
      await boothStub(this.env, boothNumber).fetch("https://booth/internal/reset", jsonRequest({ boothNumber, reason }));
      return respondJson({ ok: true });
    }

    await roomStub(this.env, callId).fetch("https://call/internal/close", jsonRequest({ reason }));

    const remoteNumber = boothNumber === call.callerNumber ? call.calleeNumber : call.callerNumber;
    await Promise.allSettled([
      boothStub(this.env, boothNumber).fetch("https://booth/internal/reset", jsonRequest({ boothNumber, reason })),
      boothStub(this.env, remoteNumber).fetch("https://booth/internal/apply", jsonRequest({
        boothNumber: remoteNumber,
        lineState: LINE_STATES.IDLE,
        currentCallId: null,
        peerNumber: null,
        direction: null,
        callerNumber: call.callerNumber,
        calleeNumber: call.calleeNumber,
        callerClientId: call.callerClientId,
        eventType: remoteEventType,
        reason,
      })),
    ]);

    return respondJson({ ok: true, callId });
  }
}

async function handleRegisterBooth(request, env) {
  const body = await readJson(request);
  const boothNumber = normalizeNumber(body?.boothNumber);
  if (!boothNumber)
    return respondJson({ ok: false, error: "boothNumber required" }, 400);

  return boothStub(env, boothNumber).fetch("https://booth/internal/register", jsonRequest(body));
}

async function handleBoothEvents(boothNumber, request, env) {
  const internalUrl = new URL("https://booth/internal/events");
  internalUrl.search = new URL(request.url).search;
  return boothStub(env, boothNumber).fetch(new Request(internalUrl.toString(), request));
}

async function handleDial(request, env) {
  return switchboardStub(env).fetch("https://switchboard/internal/dial", jsonRequest(await request.text()));
}

async function handleCallAction(callId, action, request, env) {
  const internalUrl = `https://switchboard/internal/${action}?callId=${encodeURIComponent(callId)}`;
  return switchboardStub(env).fetch(internalUrl, jsonRequest(await request.text()));
}

async function handlePostSignal(callId, request, env) {
  return roomStub(env, callId).fetch("https://call/internal/signal", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: await request.text(),
  });
}

async function handleGetSignal(callId, url, env) {
  const type = url.searchParams.get("type");
  if (!type) return respondJson({ error: "type query parameter required" }, 400);
  return roomStub(env, callId).fetch(`https://call/internal/signal?type=${encodeURIComponent(type)}`, { method: "GET" });
}

async function handleDeleteSignal(callId, url, env) {
  const type = url.searchParams.get("type");
  if (!type) return respondJson({ error: "type query parameter required" }, 400);
  return roomStub(env, callId).fetch(`https://call/internal/signal?type=${encodeURIComponent(type)}`, { method: "DELETE" });
}

function boothStub(env, boothNumber) {
  const id = env.BOOTHS_DO.idFromName(boothNumber);
  return env.BOOTHS_DO.get(id);
}

function roomStub(env, callId) {
  const id = env.ROOMS_DO.idFromName(callId);
  return env.ROOMS_DO.get(id);
}

function switchboardStub(env) {
  const id = env.LOBBY_DO.idFromName(SWITCHBOARD_ID);
  return env.LOBBY_DO.get(id);
}

async function getBoothStatus(env, boothNumber) {
  const response = await boothStub(env, boothNumber).fetch("https://booth/internal/status", { method: "GET" });
  if (!response.ok)
    return { registered: false, online: false, lineState: LINE_STATES.IDLE };
  return await response.json();
}

async function getCall(env, callId) {
  const response = await roomStub(env, callId).fetch("https://call/internal/call", { method: "GET" });
  if (!response.ok) return null;
  return await response.json();
}

function buildRingingResponse(status) {
  return {
    ok: true,
    outcome: "ringing",
    callId: status.currentCallId,
    callerNumber: status.callerNumber,
    calleeNumber: status.calleeNumber,
    callerClientId: status.callerClientId,
  };
}

function defaultLine() {
  return {
    lineState: LINE_STATES.IDLE,
    currentCallId: null,
    peerNumber: null,
    direction: null,
    callerNumber: null,
    calleeNumber: null,
    callerClientId: null,
    updatedAt: Date.now(),
  };
}

function lineEvent(type, boothNumber, line, reason) {
  return {
    type,
    boothNumber,
    lineState: line.lineState,
    callId: line.currentCallId,
    peerNumber: line.peerNumber,
    callerNumber: line.callerNumber,
    calleeNumber: line.calleeNumber,
    callerClientId: line.callerClientId,
    reason: reason || null,
  };
}

function normalizeLineState(lineState) {
  return Object.values(LINE_STATES).includes(lineState) ? lineState : LINE_STATES.IDLE;
}

function signalKey(type) {
  return `signal:${type}`;
}

function normalizeNumber(value) {
  if (typeof value !== "string") return null;
  const digits = value.replace(/\D+/g, "");
  if (!digits) return null;
  return digits;
}

function normalizeString(value) {
  if (typeof value !== "string") return null;
  const trimmed = value.trim();
  return trimmed || null;
}

function normalizeCallIdFromRequest(request) {
  const url = new URL(request.url);
  return normalizeString(url.searchParams.get("callId"));
}

async function readJson(request) {
  try {
    if (typeof request === "string")
      return JSON.parse(request || "{}");
    return await request.json();
  } catch {
    return {};
  }
}

function jsonRequest(body) {
  const payload = typeof body === "string" ? body : JSON.stringify(body || {});
  return {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: payload,
  };
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
