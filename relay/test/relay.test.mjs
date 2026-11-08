import test from "node:test";
import assert from "node:assert/strict";
import { randomBytes } from "node:crypto";
import { mkdtempSync, rmSync, realpathSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname, basename } from "node:path";
import worker from "../worker/index.js";
import { openDatabase } from "../sqlite.mjs";

const secret = () => randomBytes(32).toString("hex");
const identifier = () => randomBytes(16).toString("hex");

function api(DB) {
  return async function request(path, token, data) {
    const response = await worker.fetch(new Request("http://localhost" + path, {
      method: data === undefined ? "GET" : "POST",
      headers: {
        ...(token ? { Authorization: "Bearer " + token } : {}),
        ...(data === undefined ? {} : { "Content-Type": "application/json" }),
      },
      body: data === undefined ? undefined : JSON.stringify(data),
    }), { DB });
    return { code: response.status, body: await response.json() };
  };
}

async function paired(DB) {
  const request = api(DB);
  const receiver = identifier();
  const receiverToken = secret();
  const deviceToken = secret();
  const deviceId = "02:00:00:00:00:01";
  const nonce = identifier();
  const offer = await request("/v1/pairing", receiverToken, { receiver_id: receiver, pair_nonce: nonce });
  assert.equal(offer.code, 200);
  assert.equal(offer.body.code.length, 8);
  assert.equal((await request("/v1/pair", null, {
    code: offer.body.code, device_id: deviceId, device_token: deviceToken,
  })).body.status, "paired");
  return {
    request, receiver, receiverToken, deviceToken, deviceId, nonce, offer,
    pc(path, data) { return request(`/v1/receiver/${receiver}/${path}`, receiverToken, data); },
    device(action, id, text = "") {
      return request(`/v1/device/${receiver}/${action}`, deviceToken, { device_id: deviceId, id, text });
    },
  };
}

test("pairing creation and redemption can be retried without rotating credentials", async () => {
  const DB = openDatabase();
  try {
    const f = await paired(DB);
    const repeated = await Promise.all(Array.from({ length: 3 }, () => f.request("/v1/pairing", f.receiverToken,
      { receiver_id: f.receiver, pair_nonce: f.nonce })));
    assert.ok(repeated.every(result => result.body.code === f.offer.body.code));
    assert.equal((await f.request("/v1/pair", null, {
      code: f.offer.body.code, device_id: f.deviceId, device_token: f.deviceToken,
    })).body.status, "paired");
    assert.equal((await f.request("/v1/pair", null, {
      code: f.offer.body.code, device_id: f.deviceId, device_token: secret(),
    })).code, 409);
    const room = DB.raw.prepare("SELECT * FROM rooms").get();
    assert.ok(!JSON.stringify(room).includes(f.receiverToken));
    assert.ok(!JSON.stringify(room).includes(f.deviceToken));
    assert.equal((await f.request("/v1/pairing", secret(), { receiver_id: f.receiver, pair_nonce: identifier() })).code, 401);
  } finally { DB.close(); }
});

test("Unicode commands, concurrent retries and acknowledgements are idempotent", async () => {
  const DB = openDatabase();
  try {
    const f = await paired(DB);
    const text = "跨网络语音输入。Hello 🐾";
    const deliveries = Array.from({ length: 5 }, () => f.device("text", "wan_unicode_01", text));
    const poll = await f.pc("next?wait_ms=1000");
    assert.equal(poll.body.device_id, f.deviceId);
    assert.equal(poll.body.commands.length, 1);
    const command = poll.body.commands[0];
    assert.equal(command.text, text);
    assert.ok(command.ttl_ms > 0 && command.ttl_ms <= 10000);
    assert.equal(DB.raw.prepare("SELECT COUNT(*) AS count FROM commands").get().count, 1);
    const ack = { command_id: command.command_id, status: "submitted", code: 200 };
    assert.equal((await f.pc("ack", ack)).code, 200);
    assert.equal((await f.pc("ack", ack)).code, 200);
    assert.equal((await f.pc("ack", { ...ack, status: "focus_changed" })).code, 409);
    assert.ok((await Promise.all(deliveries)).every(result => result.body.status === "submitted"));
    assert.equal((await f.device("text", "wan_unicode_01", text)).body.status, "submitted");
    assert.equal((await f.device("text", "wan_unicode_01", "另一段文字")).code, 409);
    assert.equal(DB.raw.prepare("SELECT text FROM commands").get().text, "");
    assert.equal((await f.pc("next")).body.commands.length, 0);
  } finally { DB.close(); }
});

test("roles and rooms are isolated and repairing revokes the previous device", async () => {
  const DB = openDatabase();
  try {
    const f = await paired(DB);
    const other = await paired(DB);
    assert.equal((await f.request(`/v1/receiver/${f.receiver}/next`, f.deviceToken)).code, 401);
    assert.equal((await f.request(`/v1/receiver/${other.receiver}/next`, f.receiverToken)).code, 401);
    assert.equal((await f.request(`/v1/device/${f.receiver}/begin`, f.receiverToken,
      { device_id: f.deviceId, id: "wrong_role_01" })).code, 401);
    await f.request("/v1/pairing", f.receiverToken, { receiver_id: f.receiver, pair_nonce: identifier() });
    assert.equal((await f.device("begin", "revoked_device_01")).code, 401);
  } finally { DB.close(); }
});

test("the pending queue stays bounded during concurrent unique requests", async () => {
  const DB = openDatabase();
  try {
    const f = await paired(DB);
    const deliveries = Array.from({ length: 6 }, (_, index) => f.device("text", `bounded_queue_${index}`, "并发内容"));
    const first = await f.pc("next?wait_ms=1000");
    assert.ok(first.body.commands.length > 0);
    await new Promise(resolve => setTimeout(resolve, 50));
    assert.equal(DB.raw.prepare("SELECT COUNT(*) AS count FROM commands").get().count, 4);
    const pending = await f.pc("next");
    assert.equal(pending.body.commands.length, 4);
    for (const command of pending.body.commands)
      await f.pc("ack", { command_id: command.command_id, status: "submitted", code: 200 });
    const results = await Promise.all(deliveries);
    assert.equal(results.filter(result => result.body.status === "submitted").length, 4);
    assert.equal(results.filter(result => result.body.status === "busy").length, 2);
  } finally { DB.close(); }
});

test("offline and expired commands cannot be replayed or returned to the PC", async () => {
  const DB = openDatabase();
  try {
    const f = await paired(DB);
    DB.raw.prepare("UPDATE rooms SET last_seen=? WHERE id=?").run(Date.now() - 30000, f.receiver);
    assert.equal((await f.device("begin", "offline_test_01")).body.status, "receiver_offline");
    assert.equal(DB.raw.prepare("SELECT COUNT(*) AS count FROM commands").get().count, 0);
    await f.pc("next");
    const delivery = f.device("text", "expired_test_01", "过期内容");
    const poll = await f.pc("next?wait_ms=1000");
    assert.equal(poll.body.commands.length, 1);
    DB.raw.prepare("UPDATE commands SET expires_at=?").run(Date.now() - 1);
    assert.equal((await delivery).body.status, "expired");
    assert.equal((await f.pc("next")).body.commands.length, 0);
    assert.equal((await f.device("text", "expired_test_01", "过期内容")).body.status, "expired");
    assert.equal(DB.raw.prepare("SELECT COUNT(*) AS count FROM commands").get().count, 1);
    DB.raw.prepare("UPDATE rooms SET pair_expires=?").run(Date.now() - 1);
    assert.equal((await f.request("/v1/pair", null, {
      code: f.offer.body.code, device_id: f.deviceId, device_token: f.deviceToken,
    })).body.status, "pairing_expired");
  } finally { DB.close(); }
});

test("invalid input is bounded and cannot consume a valid command", async () => {
  const DB = openDatabase();
  try {
    const f = await paired(DB);
    assert.equal((await f.device("begin", null)).code, 400);
    assert.equal((await f.device("text", "invalid_text_01", " ")).code, 400);
    assert.equal((await f.device("text", "invalid_text_01", "中".repeat(4097))).code, 400);
    assert.equal((await f.device("text", "invalid_text_01", "\0")).code, 400);
    assert.equal(DB.raw.prepare("SELECT COUNT(*) AS count FROM commands").get().count, 0);
    const oversized = await worker.fetch(new Request("http://localhost/v1/pair", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: "a".repeat(32769),
    }), { DB });
    assert.equal(oversized.status, 413);
    let limited;
    for (let attempt = 0; attempt < 31; attempt++) limited = await f.request("/v1/pair", null,
      { code: "ABCDEFGH", device_id: f.deviceId, device_token: f.deviceToken });
    assert.equal(limited.code, 429);
  } finally { DB.close(); }
});

test("pairing survives a relay process restart", async () => {
  const temporaryRoot = realpathSync(tmpdir());
  const directory = mkdtempSync(join(temporaryRoot, "passport-relay-"));
  const filename = join(directory, "relay.sqlite");
  let DB = openDatabase(filename);
  try {
    const f = await paired(DB);
    DB.close();
    DB = openDatabase(filename);
    const response = await api(DB)(`/v1/receiver/${f.receiver}/next`, f.receiverToken);
    assert.equal(response.code, 200);
    assert.equal(response.body.device_id, f.deviceId);
  } finally {
    DB.close();
    const resolved = realpathSync(directory);
    assert.equal(dirname(resolved), temporaryRoot);
    assert.ok(basename(resolved).startsWith("passport-relay-"));
    rmSync(resolved, { recursive: true, force: true });
  }
});
