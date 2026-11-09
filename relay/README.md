# Passport Dictation relay

This relay forwards dictation control commands and recognized text between the device and Windows. The standalone Node service uses persistent SQLite storage. The same request handlers also support a Worker with a D1 binding named `DB`. The shipped clients use the service below.

[Default hosted relay](http://8.133.186.187:23000/passport-dictation)

The default service runs on a mainland server at `http://8.133.186.187:23000/passport-dictation`. It uses HTTP as selected for this deployment. Both endpoints retain the complete port and project path. Upgrades from the former default require re-pairing.

## Local development

Use Node.js 22.13 or newer. Production code has no npm dependencies. Development dependencies generate database migrations.

```powershell
npm ci
npm test
npm run dev
```

The development adapter listens on loopback port 18767 and uses an in-memory SQLite database. Set `DATABASE_PATH` to a file outside the source checkout for persistent local testing. `HOST` and `PORT` configure the listener. Normal clients and the headless protocol harness accept HTTP or HTTPS, including project subpaths.

## Deployment

Run `npm run build` to emit `dist/server/index.js`, a Cloudflare Worker ES module. Configure a D1 database binding named `DB` and apply the generated migrations in `drizzle/` before serving requests. Keep the database and authentication state private. Use a public HTTP or HTTPS service address accessible from both networks and configure that same address on the PC and device.

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

The relay temporarily stores recognized text in plaintext and transports it using the configured HTTP or HTTPS address. The shipped default uses HTTP. Acknowledgement clears the command's text. Expiry blocks later delivery. Command rows older than expiry plus two minutes are removed opportunistically during receiver activity, in bounded batches. Inactive rooms expire after 30 days and are also cleaned during activity. The service logs request failure types only. It carries recognized text and control messages, while speech recognition uses the device's separate XiaoZhi cloud connection.

Pair-code redemption and room creation have per-IP rate limits. Receiver presence expires after 25 seconds without a poll. Re-pairing or changing the relay requires updating both endpoints' settings.

## Standalone server deployment

The current server uses `/jjjworkspace/passport-dictation` with `current` pointing to an immutable directory under `releases/`. Its dedicated Node runtime lives in `runtime/node/`, SQLite in `data/relay.sqlite`, and deployment records in `deployments/`.

`deploy/passport-dictation.service` runs the listener at `127.0.0.1:18767` as the `passport-dictation` service account. The service is enabled at boot and restarts on failure. It can write only to its data directory. The current runtime is Node 22.23.4, independently copied with its license from the server's existing runtime.

The shared OpenResty container `jjjworkspace-web` serves port `23000` with `/jjjworkspace` mounted read-only. Its main configuration includes `/jjjworkspace/.hosting/projects.d/*.conf`. The project snippet is `deploy/passport-dictation.conf`, installed as `/jjjworkspace/.hosting/projects.d/passport-dictation.conf`. It strips `/passport-dictation/` before forwarding to the backend, retains authorization headers and query strings, disables response caching and buffering, and passes the actual client IP for rate limiting.

Additional projects use their own directories and uniquely named snippets in the same include directory. Static projects can use an alias. Applications can use their own loopback ports and path-prefixed proxy locations. Acquire `/jjjworkspace/.hosting/publish.lock` before changing shared routing, validate the configuration, reload it and wait for the new route's health check to succeed.

```sh
sudo systemctl status passport-dictation.service
sudo journalctl -u passport-dictation.service -n 50
sudo systemctl restart passport-dictation.service
sudo docker exec jjjworkspace-web /usr/local/openresty/bin/openresty -t -c /jjjworkspace/.hosting/nginx.conf
sudo docker exec jjjworkspace-web /usr/local/openresty/bin/openresty -s reload -c /jjjworkspace/.hosting/nginx.conf
curl http://127.0.0.1:23000/passport-dictation/health
```

Service restart and pairing persistence have been verified. The complete voice-to-native-input workflow still requires physical-device testing.
