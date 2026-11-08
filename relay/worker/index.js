const encoder = new TextEncoder();
const roomPattern = /^[0-9a-f]{32}$/;
const tokenPattern = /^[0-9a-f]{64}$/;
const idPattern = /^[a-zA-Z0-9_-]{8,64}$/;
const devicePattern = /^(?:[0-9a-f]{2}:){5}[0-9a-f]{2}$/i;
const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

class RequestError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

function json(value, status = 200) {
  return Response.json(value, {
    status,
    headers: { "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" },
  });
}

function requireValue(condition, message = "invalid_request") {
  if (!condition) throw new RequestError(400, message);
}

async function hash(value) {
  const digest = await crypto.subtle.digest("SHA-256", encoder.encode(value));
  return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
}

function equalHash(left, right) {
  if (typeof left !== "string" || typeof right !== "string" || left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index++) difference |= left.charCodeAt(index) ^ right.charCodeAt(index);
  return difference === 0;
}

function bearer(request) {
  const value = request.headers.get("Authorization") || "";
  const token = value.startsWith("Bearer ") ? value.slice(7) : "";
  if (!tokenPattern.test(token)) throw new RequestError(401, "unauthorized");
  return token;
}

async function body(request) {
  if (!request.headers.get("Content-Type")?.startsWith("application/json")) throw new RequestError(415, "json_required");
  if (Number(request.headers.get("Content-Length")) > 32768) throw new RequestError(413, "body_too_large");
  const reader = request.body?.getReader();
  if (!reader) throw new RequestError(400, "invalid_json");
  const chunks = [];
  let size = 0;
  while (true) {
    const part = await reader.read();
    if (part.done) break;
    size += part.value.length;
    if (size > 32768) {
      await reader.cancel();
      throw new RequestError(413, "body_too_large");
    }
    chunks.push(part.value);
  }
  const bytes = new Uint8Array(size);
  let position = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, position);
    position += chunk.length;
  }
  try {
    const result = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
    requireValue(result !== null && typeof result === "object" && !Array.isArray(result));
    return result;
  } catch (error) {
    if (error instanceof RequestError) throw error;
    throw new RequestError(400, "invalid_json");
  }
}

async function limit(request, env, purpose, maximum) {
  const minute = Math.floor(Date.now() / 60000);
  const key = await hash((request.headers.get("CF-Connecting-IP") || "local") + purpose + minute);
  const result = await env.DB.prepare(
    "INSERT INTO rate_limits (key,count,expires_at) VALUES (?,1,?) " +
    "ON CONFLICT(key) DO UPDATE SET count=count+1 RETURNING count"
  ).bind(key, (minute + 2) * 60000).first();
  if (result.count > maximum) throw new RequestError(429, "rate_limited");
}

async function authorizedRoom(request, env, id, role) {
  requireValue(roomPattern.test(id));
  const key = await hash(bearer(request));
  const room = await env.DB.prepare("SELECT * FROM rooms WHERE id=?").bind(id).first();
  if (!room || !equalHash(key, room[role + "_hash"])) throw new RequestError(401, "unauthorized");
  return room;
}

async function cleanup(env, room) {
  const now = Date.now();
  if (room.cleanup_at > now - 60000) return;
  await env.DB.batch([
    env.DB.prepare("UPDATE rooms SET cleanup_at=? WHERE id=?").bind(now, room.id),
    env.DB.prepare("DELETE FROM commands WHERE key IN (SELECT key FROM commands WHERE expires_at<? LIMIT 100)").bind(now - 120000),
    env.DB.prepare("DELETE FROM rate_limits WHERE key IN (SELECT key FROM rate_limits WHERE expires_at<? LIMIT 100)").bind(now),
    env.DB.prepare("DELETE FROM rooms WHERE id IN (SELECT id FROM rooms WHERE last_seen<? LIMIT 10)").bind(now - 30 * 86400000),
  ]);
}

async function pairing(request, env) {
  const data = await body(request);
  requireValue(roomPattern.test(data.receiver_id) && roomPattern.test(data.pair_nonce));
  const receiverHash = await hash(bearer(request));
  const previous = await env.DB.prepare("SELECT * FROM rooms WHERE id=?").bind(data.receiver_id).first();
  if (previous && !equalHash(previous.receiver_hash, receiverHash)) throw new RequestError(401, "unauthorized");
  if (!previous) await limit(request, env, "create", 10);
  const code = Array.from(crypto.getRandomValues(new Uint8Array(8)), byte => alphabet[byte & 31]).join("");
  const now = Date.now();
  await env.DB.batch([
    env.DB.prepare("DELETE FROM commands WHERE room_id=? AND EXISTS " +
      "(SELECT 1 FROM rooms WHERE id=? AND receiver_hash=? AND pair_nonce<>?)")
      .bind(data.receiver_id, data.receiver_id, receiverHash, data.pair_nonce),
    env.DB.prepare("INSERT INTO rooms (id,receiver_hash,device_hash,device_id,pair_code,pair_nonce,pair_expires,last_seen,cleanup_at,created_at) " +
      "VALUES (?,?,NULL,NULL,?,?,?,?,?,?) ON CONFLICT(id) DO UPDATE SET " +
      "device_hash=NULL,device_id=NULL,pair_code=excluded.pair_code,pair_nonce=excluded.pair_nonce," +
      "pair_expires=excluded.pair_expires,last_seen=excluded.last_seen " +
      "WHERE rooms.receiver_hash=excluded.receiver_hash AND rooms.pair_nonce<>excluded.pair_nonce")
      .bind(data.receiver_id, receiverHash, code, data.pair_nonce, now + 600000, now, now, now),
  ]);
  const room = await env.DB.prepare("SELECT * FROM rooms WHERE id=?").bind(data.receiver_id).first();
  if (room.pair_nonce !== data.pair_nonce) throw new RequestError(409, "pairing_changed");
  if (room.pair_expires <= now) throw new RequestError(409, "pairing_expired");
  return json({ receiver_id: room.id, code: room.pair_code, expires_in: Math.floor((room.pair_expires - now) / 1000) });
}

async function redeem(request, env) {
  await limit(request, env, "redeem", 30);
  const data = await body(request);
  const code = typeof data.code === "string" ? data.code.toUpperCase().replaceAll("-", "").trim() : "";
  requireValue(code.length === 8 && Array.from(code).every(letter => alphabet.includes(letter)));
  requireValue(typeof data.device_id === "string" && devicePattern.test(data.device_id));
  requireValue(tokenPattern.test(data.device_token));
  const deviceId = data.device_id.toLowerCase();
  const deviceHash = await hash(data.device_token);
  const now = Date.now();
  const room = await env.DB.prepare("SELECT * FROM rooms WHERE pair_code=? AND pair_expires>?").bind(code, now).first();
  if (!room) throw new RequestError(409, "pairing_expired");
  await env.DB.prepare("UPDATE rooms SET device_hash=?,device_id=? WHERE id=? AND pair_code=? AND pair_expires>? AND device_hash IS NULL")
    .bind(deviceHash, deviceId, room.id, code, now).run();
  const claimed = await env.DB.prepare("SELECT * FROM rooms WHERE id=?").bind(room.id).first();
  if (claimed.pair_code !== code || !equalHash(claimed.device_hash, deviceHash) || claimed.device_id !== deviceId)
    throw new RequestError(409, "pairing_used");
  return json({ receiver_id: room.id, status: "paired" });
}

async function next(request, env, id) {
  const room = await authorizedRoom(request, env, id, "receiver");
  const now = Date.now();
  if (room.last_seen < now - 5000)
    await env.DB.prepare("UPDATE rooms SET last_seen=? WHERE id=?").bind(now, id).run();
  await cleanup(env, room);
  const wait = Math.min(15000, Math.max(0, Number(new URL(request.url).searchParams.get("wait_ms")) || 0));
  const deadline = Date.now() + wait;
  let commands;
  do {
    const result = await env.DB.prepare("SELECT key,id,action,text,expires_at FROM commands " +
      "WHERE room_id=? AND result_status IS NULL AND expires_at>? ORDER BY created_at,key LIMIT 4")
      .bind(id, Date.now()).all();
    commands = result.results;
    if (commands.length || Date.now() >= deadline || request.signal.aborted) break;
    await delay(Math.min(500, Math.max(1, deadline - Date.now())));
  } while (true);
  const current = await env.DB.prepare("SELECT device_id FROM rooms WHERE id=?").bind(id).first();
  return json({ device_id: current?.device_id || "", commands: commands.map(command => ({
    command_id: command.key, id: command.id, action: command.action, text: command.text,
    ttl_ms: Math.max(0, command.expires_at - Date.now()),
  })) });
}

async function acknowledge(request, env, id) {
  await authorizedRoom(request, env, id, "receiver");
  const data = await body(request);
  requireValue(roomPattern.test(data.command_id) && typeof data.status === "string" && /^[a-z_]{1,40}$/.test(data.status));
  requireValue(Number.isInteger(data.code) && data.code >= 200 && data.code <= 599);
  await env.DB.prepare("UPDATE commands SET result_status=?,result_code=?,text='' WHERE key=? AND room_id=? AND result_status IS NULL")
    .bind(data.status, data.code, data.command_id, id).run();
  const command = await env.DB.prepare("SELECT result_status,result_code FROM commands WHERE key=? AND room_id=?")
    .bind(data.command_id, id).first();
  if (!command) throw new RequestError(404, "unknown_command");
  if (command.result_status !== data.status || command.result_code !== data.code) throw new RequestError(409, "conflicting_ack");
  return json({ status: "acknowledged" });
}

async function deliver(request, env, id, action) {
  const room = await authorizedRoom(request, env, id, "device");
  const data = await body(request);
  requireValue(typeof data.device_id === "string" && data.device_id.toLowerCase() === room.device_id && typeof data.id === "string" && idPattern.test(data.id));
  const text = action === "text" ? data.text : "";
  requireValue(typeof text === "string" && text.length <= 4096 && !text.includes("\0"));
  if (action === "text") requireValue(text.trim().length > 0, "invalid_text");
  const key = (await hash(id + "\n" + data.id + "\n" + action)).slice(0, 32);
  const payloadHash = await hash(JSON.stringify({ id: data.id, action, text }));
  let command = await env.DB.prepare("SELECT * FROM commands WHERE key=?").bind(key).first();
  if (!command) {
    if (room.last_seen < Date.now() - 25000) return json({ id: data.id, status: "receiver_offline" }, 409);
    const now = Date.now();
    await env.DB.prepare("INSERT OR IGNORE INTO commands (key,room_id,id,action,text,payload_hash,created_at,expires_at) " +
      "SELECT ?,?,?,?,?,?,?,? WHERE (SELECT COUNT(*) FROM commands WHERE room_id=? AND expires_at>? AND result_status IS NULL)<4")
      .bind(key, id, data.id, action, text, payloadHash, now, now + 10000, id, now).run();
    command = await env.DB.prepare("SELECT * FROM commands WHERE key=?").bind(key).first();
    if (!command) return json({ id: data.id, status: "busy" }, 409);
  }
  if (command.payload_hash !== payloadHash) return json({ id: data.id, status: "conflicting_text" }, 409);
  while (!command.result_status && command.expires_at > Date.now() && !request.signal.aborted) {
    await delay(300);
    command = await env.DB.prepare("SELECT * FROM commands WHERE key=?").bind(key).first();
    if (!command) return json({ id: data.id, status: "cancelled" }, 409);
  }
  return command.result_status
    ? json({ id: data.id, status: command.result_status }, command.result_code)
    : json({ id: data.id, status: "expired" }, 408);
}

export default {
  async fetch(request, env) {
    try {
      const path = new URL(request.url).pathname.split("/").filter(Boolean);
      if (request.method === "GET" && (path.length === 0 || path.join("/") === "health"))
        return json({ service: "passport-dictation-relay", version: 2 });
      if (!env.DB) throw new RequestError(503, "storage_unavailable");
      if (request.method === "POST" && path.join("/") === "v1/pairing") return await pairing(request, env);
      if (request.method === "POST" && path.join("/") === "v1/pair") return await redeem(request, env);
      if (path.length === 4 && path[0] === "v1" && path[1] === "receiver") {
        if (request.method === "GET" && path[3] === "next") return await next(request, env, path[2]);
        if (request.method === "POST" && path[3] === "ack") return await acknowledge(request, env, path[2]);
      }
      if (request.method === "POST" && path.length === 4 && path[0] === "v1" && path[1] === "device" && ["begin", "text", "cancel"].includes(path[3]))
        return await deliver(request, env, path[2], path[3]);
      return json({ status: "not_found" }, 404);
    } catch (error) {
      if (error instanceof RequestError) return json({ status: error.message }, error.status);
      console.error("Relay request failed", error?.name || "Error");
      return json({ status: "relay_error" }, 500);
    }
  },
};
