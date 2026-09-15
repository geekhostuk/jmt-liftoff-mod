# Command & event reference

The complete message vocabulary between a bot plugin and the competition server.
Machine-readable schemas live in [`../contracts/`](../contracts/); this page is the
human-readable companion with worked JSON examples.

Conventions:

- Every plugin→server **event** has an `event_type` field. Most carry the shared
  envelope from [`contracts/common.json`](../contracts/common.json):
  `timestamp_utc`, `session_id`, `race_id`, `race_ordinal`, `event_ordinal`
  (elided from the examples below as `"…"` metadata).
- Every server→plugin **command** has a `cmd` field and should carry a unique
  `command_id`; the plugin answers with exactly one `command_ack` per
  `command_id` (see [server-protocol.md](server-protocol.md)).
- Commands execute on the Unity main thread. Commands marked **staleable** are
  skipped (acked `skipped_stale`) if the main thread picks them up more than
  30 s after they arrived.
- Commands marked **host only** are refused, acked `not_host` with nothing else
  done, when the game is a guest in a room someone else hosts. See
  [Host only](server-protocol.md#host-only).

## Server → plugin commands

### Track / room control

| `cmd` | Staleable | Host only | Payload fields | Effect |
|---|---|---|---|---|
| `set_track` | ✔ | ✔ | `env`, `track`, `race`, `workshop_id` (optional) | Apply a specific environment/track/race — and optionally Steam Workshop content — through Liftoff's multiplayer settings flow. Schema: [`set_track.json`](../contracts/set_track.json) |
| `next_track` | ✔ | ✔ | — | Advance to the next track in the bot's configured rotation sequence. |
| `update_playlist` | ✔ | with `apply_immediately` | `sequence`, `apply_immediately` (`"true"`/`"false"`) | Replace the bot's track rotation playlist; optionally jump to it immediately. |
| `create_game` | ✔ | — | — | Open Liftoff's create-game flow and host a new room. |
| `request_catalog` | — | — | — | Capture the full environment/track catalog from the open track-selection popup and emit a `track_catalog` event. Fails (`error` ack) if the popup isn't available. |
| `prepare_track` | — | — | `env`, `track`, `race` | **Deprecated no-op** — kept so older servers don't time out; acked `ok`/`no-op`. Schema: [`prepare_track.json`](../contracts/prepare_track.json) |
| `set_room_playlist` | — | ✔ | `state` (string, at most 2048 UTF-8 bytes) | Since 1.6.0. Store the controller's playlist state on the room, as the room properties `JMTP` and `JMTPt` (the server time it was written), so the room's next host can carry the playlist on. The state is an opaque string owned by the controller. Acked `error` with `not in a room` or `state too long`. See [Room playlist and handover](server-protocol.md#room-playlist-and-handover). Schema: [`set_room_playlist.json`](../contracts/set_room_playlist.json) |

```json
{ "cmd": "set_track", "command_id": "st-1786800123",
  "env": "StrawBale", "track": "Blockchain", "race": "BlockChain", "workshop_id": "" }
```

```json
{ "cmd": "set_room_playlist", "command_id": "rp-1786800124", "state": "…" }
```

### Lobby / player management

| `cmd` | Staleable | Host only | Payload fields | Effect |
|---|---|---|---|---|
| `send_chat` | ✔ | ✔ | `message` | Send a chat message into the room as the bot. |
| `kick_player` | — | ✔ | `actor` (integer) | Kick the player with that Photon actor number; outcome reported via a `kick_result` event. |
| `leave_lobby` | — | — | — | `PhotonNetwork.LeaveRoom()` — leave the current room. |
| `disconnect` | — | — | — | `PhotonNetwork.Disconnect()` — drop the Photon connection entirely. |
| `navigate_to_lobby` | ✔ | — | — | Navigate the game UI back to the multiplayer lobby screen. |

### Status

| `cmd` | Staleable | Host only | Payload fields | Effect |
|---|---|---|---|---|
| `request_lobby_status` | ✔ | — | — | Emit a fresh `lobby_status` snapshot. |
| `request_stats` | — | — | — | Ack-only liveness probe (`ok` if the socket thread is responsive). |

Unknown `cmd` values are logged and ignored — no ack is sent, so treat ack
timeout as "unsupported or unreachable".

## Plugin → server events

### Session & health

| `event_type` | When | Key fields |
|---|---|---|
| [`session_started`](../contracts/session_started.json) | On every (re)connect | `plugin`, `version`, `buildMarker` |
| [`keepalive`](../contracts/keepalive.json) | Every 60 s while in a room | `actor`, `race_id`, `race_ordinal`, `main_thread_gap_ms`, `current_env/track/race`, `room_name`, `player_count`, `max_players`, `is_host`, `is_open`, `workshop_id` |
| [`command_ack`](../contracts/command_ack.json) | Once per `command_id` | `status` (`ok` / `error` / `skipped_stale` / `not_host`), `message`, `timing_total_ms`, `timing_queue_ms`, `timing_phases`, read-back `current_env/track/race` |

### Race telemetry

Only the room host's copy of the plugin sends these, each with `is_host: true`. A
guest's copy writes them to its own race log with `is_host: false`. See
[Host only](server-protocol.md#host-only).

| `event_type` | Source | Key fields |
|---|---|---|
| [`lap_recorded`](../contracts/lap_recorded.json) | The pilot's GMS player property (`source: gms`) or Photon event 200 (`source: event200`). Both usually report the same crossing, and the second report is dropped | `actor`, `nick`, `pilot_guid`, `steam_id`, `lap_number`, `lap_ms`, `delta_prev_ms`, `delta_best_ms`, `source`, `is_host` |
| [`lap_splits`](../contracts/lap_splits.json) | The pilot's JMT Liftoff Leaderboard plugin publishes the lap's gate times on its player (`JMTG`, `JMTS`); sent after the lap's `lap_recorded`. See [Gate splits from the room](server-protocol.md#gate-splits-from-the-room) | `actor`, `nick`, `steam_id`, `lap_number`, `lap_event_ordinal`, `lap_ms`, `split_lap_ms`, `prev_lap_ms`, `gates`, `times`, `seq`, `format`, `is_host` |
| [`pilot_active`](../contracts/pilot_active.json) | The drone moves (event 201) or a player property changes; at most once every 5 s per pilot | `actor`, `nick`, `source` (`movement` / `property_change`), `detail` |
| [`pilot_reset`](../contracts/pilot_reset.json) | GMS player property, as the drone respawns | `actor`, `nick`, `reason` (`respawn` / `gms_series_mismatch`); for `respawn` also `attempt_ms`, `attempt_from` (`lap` / `respawn`), `laps_in_run`; `is_host` |
| [`pilot_complete`](../contracts/pilot_complete.json) | A pilot finishes: their race state says so, or they reach the lap cap | `actor`, `nick`, `pilot_guid`, `reason` (`race_state_finished` / `lap_count_reached`), `laps_logged`, `lap_times_ms`, `total_ms`, `is_host` |
| [`race_end`](../contracts/race_end.json) | Every pilot in the race has finished | `participants`, `completed`, `winner_actor`, `winner_nick`, `winner_total_ms`, `is_host` |
| [`race_reset`](../contracts/race_reset.json) | A new race starts | `reason` (`track_change` / `room_track_change` / `room_sgso_start` / `actor_<n>_rs_reset` / `host_takeover`), `previous_race_id`, `is_host` |

`pilot_active` is not host only.

```json
{ "event_type": "lap_recorded", "timestamp_utc": "…", "session_id": "…",
  "race_id": "r-42", "race_ordinal": 3, "event_ordinal": 127,
  "actor": 4, "nick": "PilotOne", "pilot_guid": "ab12…", "steam_id": "7656119…",
  "source": "event200", "lap_number": 2, "lap_ms": 41873, "lap_sec": 41.873,
  "delta_prev_ms": -512, "delta_best_ms": 0, "is_host": true }
```

### Lobby & players

| `event_type` | When | Key fields |
|---|---|---|
| [`player_entered`](../contracts/player_entered.json) / [`player_left`](../contracts/player_left.json) | Player joins/leaves the room | `actor`, `nick`, `user_id` |
| [`player_list`](../contracts/player_list.json) | Snapshot on change | `players[]` (`actor`, `nick`, `user_id`) |
| [`lobby_status`](../contracts/lobby_status.json) | On enter/leave, when the game joins or leaves a room or becomes or stops being its host, or on `request_lobby_status` | `in_room`, `in_lobby`, `room_name`, `player_count`, `max_players`, `is_host`, `is_open`, `is_visible`, `network_state`, `current_env/track/race` |
| [`chat_message`](../contracts/chat_message.json) | Player chats (on receipt — backlog is not replayed, see [chat-capture.md](chat-capture.md)) | `actor`, `user_id`, `nick`, `message`, `chat_id`, `self` |
| [`kick_result`](../contracts/kick_result.json) | After `kick_player` | `actor`, `nick`, `success`, `reason` |
| [`room_playlist`](../contracts/room_playlist.json) | Since 1.6.0. The room's playlist state (`JMTP`) changes, the game joins a room, the game takes over as host (before its `race_reset`), or the controller connects. Not host only. See [Room playlist and handover](server-protocol.md#room-playlist-and-handover) | `in_room`, `is_host`, `state`, `age_ms` |

### Track state

| `event_type` | When | Key fields |
|---|---|---|
| [`track_catalog`](../contracts/track_catalog.json) | After `request_catalog` | `environments[]` → `{ name, tracks[] → { name, race, workshop_id } }` |
| [`track_changed`](../contracts/track_changed.json) | The room names a new track, whether a command or the host in game changed it. After an in-game change (`commanded: false`) a `race_reset` with `room_track_change` follows | `env`, `track`, `race`, `workshop_id`, `commanded` |

### Server-side broadcast events

The remaining schemas in `contracts/` (`competition_*`, `playlist_state`,
`state_snapshot`) describe events the
**server** derives and broadcasts to browser/dashboard clients — they are part of
the wider contract set for reference, but are not sent by the plugins.
`playlist_state` is legacy: it was the old competition server's playlist
broadcast, and nothing sends it now. The playlist a room is running travels as
`room_playlist`.
