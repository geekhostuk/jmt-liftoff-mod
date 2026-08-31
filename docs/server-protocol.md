# Server protocol — linking a bot to a server

The plugins are clients: each maintains **one persistent WebSocket connection**
to a competition server and speaks newline-free JSON messages — every WebSocket
text frame is exactly one JSON object. The full vocabulary is listed in
[command-reference.md](command-reference.md) and schema-defined in
[`../contracts/`](../contracts/).

The competition server itself is not part of this repo. Anything that implements
this protocol can control a bot — the reference implementation is a Node.js
server, but the contract below is all a bot needs.

## Connection

```
Endpoint : <ServerUrl>            e.g. wss://example.com/ws/plugin
Header   : Authorization: Bearer <ApiKey>
```

- `ServerUrl` and `ApiKey` come from the plugin's `.cfg`
  (`[Competition]` section). The connection is skipped entirely if
  `Enabled = false` or the key is empty (a warning is logged).
- The server maps the API key to a bot identity (`bot_id`, nickname). One
  connection per bot; a server may accept many bots at once.
- **Reconnect:** on any error or close, the plugin retries every 5 s
  (compiled default), forever. All state (session id, race context) survives
  reconnects.

## Session lifecycle

1. On connect, the plugin emits **`session_started`**:

   ```json
   { "event_type": "session_started", "timestamp_utc": "2026-07-23T09:00:00.000Z",
     "session_id": "d3f0…", "plugin": "JMT Liftoff Mod",
     "version": "1.0.0", "buildMarker": "1.0.0+a1b2c3d" }
   ```

   The version/build marker are compiled constants — the server always knows
   which build is connected.

2. **Events** stream from plugin → server as things happen in-game (laps, gates,
   players, chat, track changes). Events are queued in an outbox (bounded at
   10,000; overflow is dropped and counted) and flushed in order whenever the
   socket is up.

3. **`keepalive`** is sent every 60 s (compiled default) while the bot is in a
   room. It doubles as the bot's status report:

   ```json
   { "event_type": "keepalive", "timestamp_utc": "…", "session_id": "…",
     "actor": 1, "race_id": "r-42", "race_ordinal": 3,
     "main_thread_last_tick_utc_ms": 1786800000000, "main_thread_gap_ms": 16,
     "current_env": "StrawBale", "current_track": "Blockchain",
     "current_race": "BlockChain", "room_name": "My Room",
     "player_count": 5, "max_players": 8, "is_host": true, "is_open": true }
   ```

   `main_thread_gap_ms` is the age of the last Unity main-thread tick — a large
   gap tells the server the game is hung even though the socket is alive
   (`-1` = liveness not wired yet during early startup).

## Commands and acknowledgements

The server sends commands as JSON objects with a `cmd` field and a caller-chosen
`command_id`:

```json
{ "cmd": "set_track", "command_id": "st-1786800123",
  "env": "StrawBale", "track": "Blockchain", "race": "BlockChain", "workshop_id": "" }
```

Rules the plugin follows:

- Commands are parsed on the socket thread but **executed on the Unity main
  thread** (game APIs are not thread-safe).
- Every command with a `command_id` produces exactly one **`command_ack`**:

  ```json
  { "event_type": "command_ack", "command_id": "st-1786800123", "status": "ok",
    "message": "", "timing_total_ms": 812, "timing_queue_ms": 3,
    "current_env": "StrawBale", "current_track": "Blockchain", "current_race": "BlockChain" }
  ```

  `status` is `ok`, `error` (with a human-readable `message`), or
  `skipped_stale`. Acks for state-changing commands include the bot's *actual*
  current env/track/race read back from Photon room properties, so the server
  can verify the change landed rather than trusting the ack.

- **Timing** (`timing_total_ms`, `timing_queue_ms`, `timing_phases`) is attached
  by `set_track` and `send_chat`, the two commands that do real work on the
  Unity main thread. `timing_total_ms` is measured from arrival on the socket
  thread, so **`total - queue` is how long the game was actually blocked** —
  and therefore how big a frame hitch the command cost everyone in the lobby.
  Worth logging: it is the only direct measure of the in-game cost of a bot.

- **Staleness:** state-changing commands (`set_track`, `next_track`,
  `create_game`, `send_chat`, `update_playlist`, `request_lobby_status`,
  `navigate_to_lobby`) are dropped if the main thread picks them up more than
  30 s after queuing — e.g. after a long hang — and acked `skipped_stale`.
  Design your server to re-issue, not to assume delivery equals execution.
- Unknown `cmd` values are logged and ignored (no ack), so the protocol is
  forward-compatible: a newer server can probe with new commands without
  crashing older bots.

## Implementing your own server — minimum viable

1. Accept WebSocket connections on a path of your choice; authenticate the
   `Authorization: Bearer` header against your bot registry.
2. Parse each incoming text frame as one JSON object; route on `event_type`.
3. To control the bot, send command objects with unique `command_id`s and await
   the matching `command_ack` (with a timeout — the bot may be hung or the
   command may be dropped as stale).
4. Track bot health from `keepalive` (presence, room state, `main_thread_gap_ms`).

Validate messages against the schemas in [`../contracts/`](../contracts/) —
`common.json` holds the shared event envelope (`event_type`, `timestamp_utc`,
`session_id`).

For hands-on examples of driving a bot, see [test-commands.sh](test-commands.sh)
(written for the reference server's internal API, but the JSON payloads it sends
are exactly the command objects described here).
