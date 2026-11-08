import { createServer } from "node:http";
import worker from "./worker/index.js";
import { openDatabase } from "./sqlite.mjs";

const host = process.env.HOST || "127.0.0.1";
const port = Number(process.env.PORT || "18767");
const DB = openDatabase(process.env.DATABASE_PATH || ":memory:");
const server = createServer(async (incoming, outgoing) => {
  try {
    const controller = new AbortController();
    outgoing.on("close", () => controller.abort());
    const request = new Request(`http://${incoming.headers.host}${incoming.url}`, {
      method: incoming.method,
      headers: incoming.headers,
      body: ["GET", "HEAD"].includes(incoming.method) ? undefined : incoming,
      duplex: "half",
      signal: controller.signal,
    });
    const response = await worker.fetch(request, { DB });
    outgoing.writeHead(response.status, Object.fromEntries(response.headers));
    outgoing.end(Buffer.from(await response.arrayBuffer()));
  } catch {
    outgoing.writeHead(500, { "Content-Type": "application/json" });
    outgoing.end('{"status":"relay_error"}');
  }
});
server.listen(port, host, () => process.stdout.write(`Relay listening on http://${host}:${port}\n`));

for (const signal of ["SIGINT", "SIGTERM"]) {
  process.on(signal, () => server.close(() => { DB.close(); process.exit(0); }));
}
