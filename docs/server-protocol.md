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

2. **Events** stream from plugin → server as things happen in-game (laps, resets,
   players, chat, track changes). Events are queued in an outbox (bounded at
   10,000; overflow is dropped and counted) and flushed in order whenever the
   socket is up.

   **Track changes.** A race runs from one `race_reset` to the next.
   - A `set_track` or `next_track` starts its race as the command arrives
     (`reason: "track_change"`), before the game has loaded the new track, so
     the room still reports the old one then. Once the room names the new track,
     the plugin sends `track_changed` with `commanded: true`.
   - A track picked in game sends `track_changed` with `commanded: false`, then
     `race_reset` with `reason: "room_track_change"`.

   A server that labels a race with the room's track should take the track from
   `track_changed` (or the next `keepalive`), not from what it held at the
   `race_reset`.

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

## Host only

Everyone in a room can run the plugin, and before 1.4.0 two copies in one room
reported every lap twice. Now only the copy in the **host's** game (the Photon
master client) acts for the room. A copy in a guest's game, in a room someone
else hosts:

- **sends no timing.** `lap_recorded`, `pilot_reset`, `pilot_complete`,
  `race_end`, `race_reset` and `lap_splits` go to its own race log, stamped
  `is_host: false`, and not to the server. A copy that sends them stamps them
  `is_host: true`.
- **runs no track, chat or kick command.** `next_track`, `set_track`,
  `send_chat`, `kick_player`, `update_playlist` with `apply_immediately`, and
  `set_room_playlist`, are acked `not_host` and do nothing else: no race starts and
  nothing changes.
- still sends everything else: chat, players, `lobby_status`, `keepalive`,
  `track_changed`, `room_playlist`.

Outside a room nothing is anyone else's to do, so commands run as they always did.

`lobby_status` is sent whenever the game joins or leaves a room, or becomes or
stops being its host: `in_room: true` with `is_host: false` is a guest. The next
`keepalive`'s `is_host` says the same.

**Taking over.** A guest's copy keeps following every pilot's laps. When the host
leaves and the room makes this game its host, the plugin sends `lobby_status`
(`is_host: true`), then `room_playlist` with the playlist the room was running
(see [Room playlist and handover](#room-playlist-and-handover)), then starts a race
with `race_reset` `reason: "host_takeover"`, then a fresh `player_list`. Every pilot's run carries on: the
laps flown before the takeover were the old host's to report and are not sent
again, and lap numbers start again at 1. A lap crossed in the moment between the
old host leaving and the switch can be lost.

## Gate splits from the room

The plugin can't see which gates a pilot flies through. The
[JMT Liftoff Leaderboard](https://github.com/geekhostuk/jmt-liftoff-leaderboard)
plugin sees its own pilot's, and after each lap publishes them on its Photon
player, in one `SetCustomProperties` call:

| Property | Type | Contents |
|---|---|---|
| `JMTG` | `string[]` | The lap's gate ids (the checkpoints' GUIDs from the course's `.race` file), in the order flown. Sent only when they differ from the last ones published, or the room changed |
| `JMTS` | `int[]` | `format` (1), `seq`, `gatesHash`, `lap_ms`, `prev_lap_ms` (0 = none), then one time per gate: milliseconds into the lap |

`gatesHash` is FNV-1a 32 (offset basis `0x811C9DC5`, prime `16777619`) over the
UTF-8 bytes of the gate ids joined by `\n`, as a signed 32-bit integer. Test
vectors: `["a", "b"]` gives `0x28E4C710`, and
`["6c91359a-89ee-4804-a012-79854b4f349d", "38cadf76-dfbd-4e56-866b-6e81f9f7569a",
"6b1e3cf7-b5fe-4c41-8d1d-3405908264df"]` gives `0x1FCDE9CE`.

The host's copy reads every pilot's. It takes `JMTG` from the update, or from the
player's properties when the update doesn't carry it, and ignores a `seq` it has
already had from that pilot. Anyone in a room can set these properties, so it
refuses a split that isn't a lap: another format, anything but one time for each
of 1 to 200 gates, a hash that doesn't match the gates, a gate id that isn't
`[A-Za-z0-9-]{1,64}`, the same gate twice in a row, or any stretch of
`0, times…, lap_ms` shorter than 40 ms.

It pairs the split with the pilot's newest lap not yet paired, within 2 ms of the
plugin's `lap_ms` and at most 60 s old, and sends
[`lap_splits`](../contracts/lap_splits.json). A split that arrives before its lap
waits up to 5 s for it: the host's own plugin can publish a moment before the
host's game records the lap. A new race drops any still waiting.

```json
{ "event_type": "lap_splits", "timestamp_utc": "…", "session_id": "…",
  "race_id": "r-42", "race_ordinal": 3, "event_ordinal": 131,
  "actor": 4, "nick": "PilotOne", "steam_id": "7656119…",
  "lap_number": 2, "lap_event_ordinal": 127, "lap_ms": 41873,
  "split_lap_ms": 41874, "prev_lap_ms": 42385,
  "gates": ["6c91359a-…", "38cadf76-…", "6b1e3cf7-…"],
  "times": [9120, 20455, 31980], "seq": 17, "format": 1, "is_host": true }
```

`lap_number` and `lap_event_ordinal` name the lap as its `lap_recorded` did, in
the same race.

## Room playlist and handover

Since 1.6.0. When the room's host leaves, the next host's controller should carry
on the playlist the room was running, where it left off. So the controller keeps
its playlist state on the Photon **room**, through the host's copy of the plugin,
and the room outlives its host. The plugin never reads the state: it is an opaque
string owned by the controller.

| Key | On | Type | Contents |
|---|---|---|---|
| `JMTP` | The room | `string` | The controller's playlist state, at most 2048 UTF-8 bytes |
| `JMTPt` | The room | `int` | `PhotonNetwork.ServerTimestamp` when `JMTP` was written |
| `JMTC` | Each player | `int` | 1 while that pilot's copy of the plugin has a controller connected. Removed when the connection drops |

**Writing it.** [`set_room_playlist`](../contracts/set_room_playlist.json) sets `JMTP`
and `JMTPt` in one `SetCustomProperties` call. Only the host's copy writes them: a
guest's acks `not_host`. The command is never dropped as stale, since only the
latest state matters and writing it is cheap.

**Reading it.** Every copy of the plugin, a guest's included, sends
[`room_playlist`](../contracts/room_playlist.json):

- when the room's `JMTP` changes, the host's own write included;
- when the game joins a room;
- when the game takes over as host, before the `race_reset` `host_takeover`;
- after `session_started`, each time the controller connects. Outside a room this
  one says `in_room: false`, with `state` and `age_ms` null.

```json
{ "event_type": "room_playlist", "timestamp_utc": "…", "session_id": "…",
  "race_id": "r-42", "race_ordinal": 3, "event_ordinal": 12,
  "in_room": true, "is_host": false, "state": "…", "age_ms": 1234 }
```

`state` is the room's `JMTP`, or null when it has none. `age_ms` is how long ago
it was written, on the Photon server's clock: `ServerTimestamp - JMTPt` in wrapping
32-bit arithmetic, and null without a state. Each game's `ServerTimestamp` is its
own estimate of the server's clock, so `age_ms` can be a few milliseconds below
zero. Anyone in a room can write its properties, so a controller should check what
it reads before acting on it.

**Marking pilots with a controller.** While the plugin is connected to a controller
it sets `JMTC` to 1 on the local player, and removes it when the connection drops.
Set outside a room, Photon keeps it on the player and sends it along when the game
joins its next room; the plugin sets it again on every join anyway. `JMTC` says
only that the pilot's plugin has a controller connected.

**Handing host on.** When this game is the room's host and leaves normally, the
plugin first makes the other player with the lowest actor number whose `JMTC` is 1
the host (`PhotonNetwork.SetMasterClient`), so the room goes to a pilot whose
controller can carry the playlist on. Leaving normally is the game leaving the room
or disconnecting from Photon (going back to the menu, moving to another room,
`leave_lobby`, `disconnect`), or the game quitting. The switch is sent at once,
ahead of the leave on the same reliable channel, so the room has its new host
before this game is gone. With no such player the plugin does nothing and Photon
picks the next host as usual; so it does after a crash or a lost connection. The
plugin hands a room on once at most, and never to itself.

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

  `status` is `ok`, `error` (with a human-readable `message`),
  `skipped_stale`, or `not_host` (the game is a guest in a room someone else
  hosts; see [Host only](#host-only)). Acks for state-changing commands include the bot's *actual*
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
