import { createServer } from "node:http";
import { isIP } from "node:net";
import worker from "./worker/index.js";
import { openDatabase } from "./sqlite.mjs";

const host = process.env.HOST || "127.0.0.1";
const port = Number(process.env.PORT || "18767");
const DB = openDatabase(process.env.DATABASE_PATH || ":memory:");
const server = createServer(async (incoming, outgoing) => {
  try {
    const controller = new AbortController();
    outgoing.on("close", () => controller.abort());
    const headers = new Headers(incoming.headers);
    const peer = incoming.socket.remoteAddress || "local";
    const forwarded = headers.get("X-Real-IP") || "";
    const localProxy = ["127.0.0.1", "::1", "::ffff:127.0.0.1"].includes(peer);
    // The standalone server supplies the same trusted IP field as the Worker
    // platform. Public request headers cannot choose their rate-limit identity.
    const address = process.env.TRUST_PROXY === "1" && localProxy && isIP(forwarded) ? forwarded : peer;
    headers.set("CF-Connecting-IP", address);
    const request = new Request(`http://${incoming.headers.host}${incoming.url}`, {
      method: incoming.method,
      headers,
      body: ["GET", "HEAD"].includes(incoming.method) ? undefined : incoming,
      duplex: "half",
      signal: controller.signal,
    });
    const response = await worker.fetch(request, { DB });
    outgoing.writeHead(response.status, Object.fromEntries(response.headers));
    outgoing.end(Buffer.from(await response.arrayBuffer()));
  } catch {
    if (!outgoing.headersSent) {
      outgoing.writeHead(500, { "Content-Type": "application/json", "Cache-Control": "no-store" });
    }
    outgoing.end('{"status":"relay_error"}');
  }
});
server.headersTimeout = 10000;
server.requestTimeout = 20000;
server.listen(port, host, () => process.stdout.write(`Relay listening on http://${host}:${server.address().port}\n`));

for (const signal of ["SIGINT", "SIGTERM"]) {
  process.on(signal, () => server.close(() => { DB.close(); process.exit(0); }));
}
