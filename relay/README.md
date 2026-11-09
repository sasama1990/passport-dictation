# Passport Dictation relay

This Worker forwards dictation control commands and recognized text between the device and Windows over outbound HTTPS connections. It uses one D1 database binding named `DB`. The shipped clients use the hosted service below.

[Default hosted relay](https://passport-dictation-relay-sasama.gabledhill.chatgpt.site)

The default hosting entry returned HTTP 403 on the tested mainland direct connection and on the physical device. A replacement entry has not been deployed. Both endpoints must be able to reach the selected relay for cross-network use.

## Local development

Use Node.js 22.13 or newer. Production code has no npm dependencies. Development dependencies generate database migrations.

```powershell
npm ci
npm test
npm run dev
```

The development adapter listens on loopback port 18767 and uses an in-memory SQLite database. Set `DATABASE_PATH` to a file outside the source checkout for persistent local testing. `HOST` and `PORT` configure the listener. Normal clients require an HTTPS relay. The headless protocol harness accepts loopback HTTP for local tests.

## Deployment

Run `npm run build` to emit `dist/server/index.js`, a Cloudflare Worker ES module. Configure a D1 database binding named `DB` and apply the generated migrations in `drizzle/` before serving requests. Keep the database and authentication state private. Use a public HTTPS origin accessible from both networks and configure that same origin on the PC and device.

The schema is defined in `db/schema.ts`. After a schema change, run `npm run db:generate`, review its generated migration, and apply it through the hosting platform's migration workflow. A Sites deployment declares `d1` as `DB` in its own `.openai/hosting.json` and includes the generated `drizzle/` directory in its deployment archive.

`GET /health` returns the service name and relay protocol version `2`.

## Pairing and command flow

All JSON requests use `Content-Type: application/json`. Receiver and device tokens are separate random 64-character lowercase hexadecimal strings. Room IDs and pairing nonces are random 32-character lowercase hexadecimal strings. Tokens are sent as bearer credentials and stored as SHA-256 hashes by the relay.

1. Windows saves its room ID, token and fresh pairing nonce locally, then calls `POST /v1/pairing` with its bearer token and a body containing `receiver_id` and `pair_nonce`.
2. The relay returns `code` and `expires_in`. The 8-character code lasts 10 minutes. An identical nonce can retry a lost response. A new nonce revokes the previous device binding and clears queued commands.
3. The device saves its own token and the pairing code before calling `POST /v1/pair` with `code`, `device_id` and `device_token`. The response contains `receiver_id` and `status`. The same device and token can retry redemption during the code's lifetime.
4. Windows polls `GET /v1/receiver/{room}/next?wait_ms=5000` with its own bearer token. It receives `device_id` and `commands`. Each command has `command_id`, utterance `id`, `action`, `text` and `ttl_ms`.
5. The device calls `POST /v1/device/{room}/begin`, `/text` or `/cancel` with its bearer token and `device_id`, utterance `id` and optional `text`. The request waits for Windows to acknowledge the command.
6. Windows calls `POST /v1/receiver/{room}/ack` with `command_id`, `status` and numeric HTTP-style `code`. The device receives the actual receiver outcome, such as `ready`, `submitted`, `focus_changed` or `expired`.

Utterance IDs contain 8–64 ASCII letters, digits, underscores or hyphens. Device IDs use colon-separated MAC-address syntax. Text is limited to 4096 JavaScript UTF-16 code units, with a 32 KiB request-body ceiling. The queue is bounded atomically to 4 pending commands per room. Commands expire after 10 seconds. Windows uses a conservative monotonic delivery deadline that subtracts the complete poll round trip from the returned TTL.

## Persistence and retention

Payload hashes and saved acknowledgements make retries idempotent. Windows also saves its delivery claim before entering text, so an interrupted delivery becomes `uncertain` after restart. An operator should inspect the input result when that occurs.

The relay temporarily stores recognized text in plaintext and transports it over HTTPS. Acknowledgement clears the command's text. Expiry blocks later delivery. Command rows older than expiry plus two minutes are removed opportunistically during receiver activity, in bounded batches. Inactive rooms expire after 30 days and are also cleaned during activity. The service logs request failure types only. It carries recognized text and control messages, while speech recognition uses the device's separate XiaoZhi cloud connection.

Pair-code redemption and room creation have per-IP rate limits. Receiver presence expires after 25 seconds without a poll. Re-pairing or changing the relay requires updating both endpoints' settings.
