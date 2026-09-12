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

## Server → plugin commands

### Track / room control

| `cmd` | Staleable | Payload fields | Effect |
|---|---|---|---|
| `set_track` | ✔ | `env`, `track`, `race`, `workshop_id` (optional) | Apply a specific environment/track/race — and optionally Steam Workshop content — through Liftoff's multiplayer settings flow. Schema: [`set_track.json`](../contracts/set_track.json) |
| `next_track` | ✔ | — | Advance to the next track in the bot's configured rotation sequence. |
| `update_playlist` | ✔ | `sequence`, `apply_immediately` (`"true"`/`"false"`) | Replace the bot's track rotation playlist; optionally jump to it immediately. |
| `create_game` | ✔ | — | Open Liftoff's create-game flow and host a new room. |
| `request_catalog` | — | — | Capture the full environment/track catalog from the open track-selection popup and emit a `track_catalog` event. Fails (`error` ack) if the popup isn't available. |
| `prepare_track` | — | `env`, `track`, `race` | **Deprecated no-op** — kept so older servers don't time out; acked `ok`/`no-op`. Schema: [`prepare_track.json`](../contracts/prepare_track.json) |

```json
{ "cmd": "set_track", "command_id": "st-1786800123",
  "env": "StrawBale", "track": "Blockchain", "race": "BlockChain", "workshop_id": "" }
```

### Lobby / player management

| `cmd` | Staleable | Payload fields | Effect |
|---|---|---|---|
| `send_chat` | ✔ | `message` | Send a chat message into the room as the bot. |
| `kick_player` | — | `actor` (integer) | Kick the player with that Photon actor number; outcome reported via a `kick_result` event. |
| `leave_lobby` | — | — | `PhotonNetwork.LeaveRoom()` — leave the current room. |
| `disconnect` | — | — | `PhotonNetwork.Disconnect()` — drop the Photon connection entirely. |
| `navigate_to_lobby` | ✔ | — | Navigate the game UI back to the multiplayer lobby screen. |

### Status

| `cmd` | Staleable | Payload fields | Effect |
|---|---|---|---|
| `request_lobby_status` | ✔ | — | Emit a fresh `lobby_status` snapshot. |
| `request_stats` | — | — | Ack-only liveness probe (`ok` if the socket thread is responsive). |

Unknown `cmd` values are logged and ignored — no ack is sent, so treat ack
timeout as "unsupported or unreachable".

## Plugin → server events

### Session & health

| `event_type` | When | Key fields |
|---|---|---|
| [`session_started`](../contracts/session_started.json) | On every (re)connect | `plugin`, `version`, `buildMarker` |
| [`keepalive`](../contracts/keepalive.json) | Every 60 s while in a room | `actor`, `race_id`, `race_ordinal`, `main_thread_gap_ms`, `current_env/track/race`, `room_name`, `player_count`, `max_players`, `is_host`, `is_open`, `workshop_id` |
| [`command_ack`](../contracts/command_ack.json) | Once per `command_id` | `status` (`ok` / `error` / `skipped_stale`), `message`, `timing_total_ms`, `timing_queue_ms`, `timing_phases`, read-back `current_env/track/race` |

### Race telemetry

| `event_type` | Source | Key fields |
|---|---|---|
| [`lap_recorded`](../contracts/lap_recorded.json) | Both plugins (Photon event 200) | `actor`, `nick`, `pilot_guid`, `steam_id`, `lap_number`, `lap_ms`, `delta_prev_ms`, `delta_best_ms`, `source` |
| [`pilot_reset`](../contracts/pilot_reset.json) | GMS player property, as the drone respawns | `actor`, `nick`, `reason` (`respawn` / `gms_series_mismatch`); for `respawn` also `attempt_ms`, `attempt_from` (`lap` / `respawn`), `laps_in_run` |
| [`gate_passed`](../contracts/gate_passed.json) | Harmony hook, in-race only | `actor`, `nick`, `checkpoint_id`, `trigger_id`, `gate_time_sec` |
| [`sector_split`](../contracts/sector_split.json) | In-race only | `actor`, `sector_index`, `from_gate`, `to_gate`, `sector_ms` |
| [`race_end`](../contracts/race_end.json) | Both | `participants`, `completed`, `winner_actor`, `winner_nick`, `winner_total_ms` |
| [`race_reset`](../contracts/race_reset.json) | A new race starts | `reason` (`track_change` / `room_track_change` / `room_sgso_start` / `actor_<n>_rs_reset`), `previous_race_id` |

```json
{ "event_type": "lap_recorded", "timestamp_utc": "…", "session_id": "…",
  "race_id": "r-42", "race_ordinal": 3, "event_ordinal": 127,
  "actor": 4, "nick": "PilotOne", "pilot_guid": "ab12…", "steam_id": "7656119…",
  "source": "event200", "lap_number": 2, "lap_ms": 41873, "lap_sec": 41.873,
  "delta_prev_ms": -512, "delta_best_ms": 0 }
```

### Lobby & players

| `event_type` | When | Key fields |
|---|---|---|
| [`player_entered`](../contracts/player_entered.json) / [`player_left`](../contracts/player_left.json) | Player joins/leaves the room | `actor`, `nick`, `user_id` |
| [`player_list`](../contracts/player_list.json) | Snapshot on change | `players[]` (`actor`, `nick`, `user_id`) |
| [`lobby_status`](../contracts/lobby_status.json) | On enter/leave/host change or `request_lobby_status` | `in_room`, `in_lobby`, `room_name`, `player_count`, `max_players`, `is_host`, `is_open`, `is_visible`, `network_state`, `current_env/track/race` |
| [`chat_message`](../contracts/chat_message.json) | Player chats (on receipt — backlog is not replayed, see [chat-capture.md](chat-capture.md)) | `actor`, `user_id`, `nick`, `message`, `chat_id`, `self` |
| [`kick_result`](../contracts/kick_result.json) | After `kick_player` | `actor`, `nick`, `success`, `reason` |

### Track state

| `event_type` | When | Key fields |
|---|---|---|
| [`track_catalog`](../contracts/track_catalog.json) | After `request_catalog` | `environments[]` → `{ name, tracks[] → { name, race, workshop_id } }` |
| [`track_changed`](../contracts/track_changed.json) | The room names a new track, whether a command or the host in game changed it. After an in-game change (`commanded: false`) a `race_reset` with `room_track_change` follows | `env`, `track`, `race`, `workshop_id`, `commanded` |

### Server-side broadcast events

The remaining schemas in `contracts/` (`competition_*`, `playlist_state`,
`state_snapshot`, `pilot_*`, `checkpoint`) describe events the
**server** derives and broadcasts to browser/dashboard clients — they are part of
the wider contract set for reference, but are not sent by the plugins.
