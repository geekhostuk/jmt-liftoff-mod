# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project follows
[Semantic Versioning](https://semver.org/) — see [docs/versioning.md](docs/versioning.md).

## [1.4.0] — 2026-09-14

Only the copy of the plugin in the room host's game acts for the room, and it sends
each pilot's gate splits along with their laps. Includes the clean-up that was to be
1.3.2.

### Added

- **Only the host's copy acts.** Two copies of the plugin in one room, one in the
  host's game and one in a guest's, reported every lap twice. Now a copy in a room
  someone else hosts sends no timing: `lap_recorded`, `pilot_reset`,
  `pilot_complete`, `race_end`, `race_reset` and `lap_splits` go to its own race
  log and not to the server. It refuses `next_track`, `set_track`, `send_chat`,
  `kick_player`, and `update_playlist` with `apply_immediately`, acking the new
  status `not_host` and doing nothing else. Those timing events carry `is_host`.
  Outside a room everything works as before.
- **A guest takes over when the host leaves.** A guest's copy keeps following every
  pilot's laps. When the room makes its game the host, it sends `lobby_status`,
  starts a race with the new `race_reset` reason `host_takeover`, and sends a fresh
  `player_list`. Every pilot's run carries on, so the laps the old host reported are
  not sent again.
- `lobby_status` is sent whenever the game joins or leaves a room, or becomes or
  stops being its host, and the keepalive's `is_host` follows at once.
- **Gate splits from the room: `lap_splits`.** The JMT Liftoff Leaderboard plugin
  publishes each lap's gate times on its pilot's Photon player (`JMTG` and `JMTS`).
  The host's copy reads everyone's, refuses any that isn't a lap, pairs each with the
  lap it recorded for that pilot (within 2 ms, and 60 s at most), and sends
  `lap_splits`, naming the lap by `lap_number` and `lap_event_ordinal`. See
  [Gate splits from the room](docs/server-protocol.md#gate-splits-from-the-room).

### Removed

- **The gate timing, which never worked.** The Harmony hook on
  `RaceCheckpoint.Trigger()` could not tell which drone passed a gate, and the
  checkpoint the plugin looked for in a pilot's `GMS` property is not there in JMT
  rooms. So `gate_passed`, `sector_split` and `checkpoint` were never sent, and
  they are gone, with their schemas. Gate timing lives in the separate
  [JMT Liftoff Leaderboard](https://github.com/geekhostuk/jmt-liftoff-leaderboard)
  plugin.
- Unused code: a second JSON serialiser, a log writer and object describer that
  the plugin's own copies had replaced, and a Photon log-noise silencer that was
  never switched on.

### Fixed

- `lap_recorded`'s `steam_id` is filled in for a pilot who was in the room before
  the plugin joined it: when the plugin never saw them enter, it asks the room.
- The docs and contracts say what the plugin sends. `lap_recorded`'s `source` is
  `gms` or `event200`. `keepalive` lists `main_thread_last_tick_utc_ms` and
  `main_thread_gap_ms`. `pilot_complete`'s `reason` is `race_state_finished` or
  `lap_count_reached`. `pilot_active`, `pilot_reset` and `pilot_complete` are
  listed as plugin events, not server-side ones.

## [1.3.1] — 2026-09-12

### Fixed

- **The Workshop id of the course being flown is sent.** `track_changed` and
  `keepalive` took it from the room property `W`, or from members named `WorkshopID`,
  `workshopId` or `SteamWorkshopId` on the room's track. Liftoff sets none of those,
  so no event ever carried one, and a server could only match a course by its name,
  which is often not its Workshop title. The room's `T` and `R` are the game's
  `GameContentEntry`, and each carries the Workshop id as `ManagedID`. The plugin now
  sends the race's, else the track's, with `W` as a last resort. Only an all-digit id
  is sent: Liftoff's own tracks have none.

### Added

- When the room names a track with no Workshop id, the log records the type and
  members of its `T` and `R` properties, so a game update that moves the id shows up
  there rather than as courses quietly losing their links.

## [1.3.0] — 2026-09-12

### Added

- **A track picked in game starts a race.** Only `set_track` and `next_track` used to
  start one, so when the host changed track from the game's own menu, the laps flown
  on the new track went on under the previous track's race. The plugin now reads the
  room's track (properties `E`, `T`, `R` and `W`) four times a second. When it names
  a different track and has held it for half a second, the plugin sends
  `track_changed` with `commanded: false`, then `race_reset` with
  `reason: "room_track_change"`. Lap events are suppressed for the same grace period
  as after a command.

- **`track_changed` is sent, as documented.** It was listed as a plugin event but
  never sent. It now goes out whenever the room names a new track, with `env`,
  `track`, `race`, `commanded` and, when the room has one, `workshop_id`.
  - A command's change arrives with `commanded: true`, and no second race. The race
    started when the command arrived, before the new track loaded and while the room
    still named the old one.
  - This is how a server learns the new track the moment the room has it, not from a
    `keepalive` up to a minute later.

  A change within 60 seconds of a track command counts as that command's.

## [1.2.0] — 2026-09-11

### Changed

- **A reset is reported the moment it happens, with the attempt it abandoned.** The
  game republishes a pilot's `GMS` player property, with no lap list, when it respawns
  their drone — about 0.6s before the drone reappears at the start. `pilot_reset` is
  now sent then, with `reason: "respawn"`, `attempt_ms` (how long the attempt had run),
  `attempt_from` (`lap` when flying on from a completed lap, `respawn` otherwise) and
  `laps_in_run`. Arriving — joining, a track change, the plugin starting — is not a
  reset, and the double publish a respawn makes is sent once.

  Until now a reset was only inferred when the pilot's *next* lap arrived and the `GMS`
  lap series no longer continued the one kept: late, silent about the attempt, and never
  at all for a pilot who reset and then left or stopped. That inference stays as a
  fallback (`reason: "gms_series_mismatch"`) for a respawn whose update never arrived.

- **One crossing is one lap.** The plugin hears most laps twice — from `GMS` and
  again from Photon event 200 — a rounding error apart and under different lap
  numbers: 546 of 564 event200 laps in a week of logs were a `GMS` lap repeated,
  519 of them 1ms quicker. Both were sent, so a pilot's lap count ran over what
  they flew and the quicker copy could become their best. A lap within 2ms of one
  the other source reported for the same pilot in the last ten minutes is now
  dropped; the copy lands a median 4s after the original but as late as 288s,
  after laps flown in between, so every recent lap is compared, not just the last.

- **`GMS` laps are compared with the pilot's current run, not every lap they have
  flown.** The list `GMS` carries holds the laps since the last respawn; it is now
  tracked on its own, so event200 laps no longer take part in the comparison.

## [1.1.0] — 2026-08-31

### Fixed

- **Chat backlog was re-emitted as live messages on every race.** `chat_message` was
  captured from `ChatWindowPanel.GenerateUserMessage`, which is a *rendering* call.
  The race scene reload disables and re-enables the chat panel, and `OnEnable` calls
  `GenerateChatFromHistory` — so the entire retained backlog was redrawn and
  republished once per race, with a fresh `timestamp_utc` and a restarted
  `event_ordinal`. Nothing in the payload let a server tell a replay from a real
  message.

  This was not just duplicate logging: a server that treats chat as input acted on
  them. A single `3` typed to vote for a track was counted again in each of the three
  following races, steering the rotation with a ballot nobody typed; `/next` and
  `/extend` were equally replayable; and the bot reacted to its own past
  announcements.

  Emission is now gated on being inside `OnChatMessageReceived`, so a redraw emits
  nothing. If that method cannot be found the plugin falls back to suppressing the
  known `GenerateChatFromHistory` redraw, and if neither is found it says so rather
  than failing silently — see [docs/chat-capture.md](docs/chat-capture.md).

### Added

- `chat_message` gains **`chat_id`** — monotonic within the session and never reset,
  so `(session_id, chat_id)` is a stable dedupe key and messages are orderable across
  races (`event_ordinal` restarts each race and cannot do that).
- `chat_message` gains **`self`** — true for the bot's own messages, so a server
  acting on chat can ignore its own announcements.
- `session_started` gains **`chat_capture_mode`** (`Receive` / `SuppressHistory` /
  `Legacy` / `None`), telling the server whether backlog suppression is actually in
  force or whether it must dedupe itself.
- [docs/chat-capture.md](docs/chat-capture.md) — the render-vs-receive call graph,
  the gating modes, and how to check for a regression after a game update.
- `scripts/release-dll.sh` — builds the plugin against a local Liftoff install and
  uploads `JmtLiftoffMod.dll`, a zip, and `SHA256SUMS.txt` to the matching GitHub
  release. It refuses to run on a dirty tree or when `HEAD` is not the tagged
  commit, so a published DLL's `buildMarker` always names a commit the release
  actually contains.

### Changed

- The two `session_started` payloads (on connect, and once at startup) are built by a
  single helper instead of being duplicated, and the startup one is now sent after
  chat capture installs so `chat_capture_mode` reports the mode actually in force.
- README now leads with what the mod is *for* — it is the in-game half of the JMT
  FPV platform and the companion mod to the unreleased JMT App, providing automatic
  track control, gate-level leaderboards and live competition data.
- Corrected the repo-scope section: it read as though no mod binary existed
  anywhere. Releases ship a built `JmtLiftoffMod.dll`; it is the *game's* DLLs that
  are never committed here.
- `docs/install.md` no longer claims Liftoff must be the Windows build — the mod
  loads under BepInEx on the native Linux build too.
- The build now finds a standard Steam install of Liftoff on Linux
  (`~/.local/share/Steam/...`, `~/.steam/steam/...`) as well as the Windows default,
  so `LIFTOFF_DIR` is only needed for non-standard layouts.
- Releases now ship a compiled DLL. Release notes point at the binary and its
  checksums instead of describing the release as source-only.

## [1.0.0] — 2026-08-31

First release of **JMT Liftoff Mod** as a standalone project. Extracted from
`LiftoffRaceBot` in [geekhostuk/liftoff-bots](https://github.com/geekhostuk/liftoff-bots)
and rebranded.

### Changed

- **Rebranded.** Plugin GUID is now `uk.co.geekhost.jmtliftoffmod` (was
  `uk.co.geekhost.liftoff.racebot`), display name `JMT Liftoff Mod`, assembly and
  DLL `JmtLiftoffMod.dll`, namespace `JmtLiftoffMod`. The config file and
  data/log folder move with the GUID. **This is not a drop-in upgrade** — see the
  upgrade table in [docs/install.md](docs/install.md#upgrading-from-liftoffracebot).
- **Version is now a real build input.** `<Version>` in `Directory.Build.props` is
  the single source of truth; a `GenerateBuildInfo` MSBuild target generates the
  `PluginVersion` and `BuildMarker` constants from it, so the `[BepInPlugin]`
  attribute, the assembly metadata and the `session_started` event can no longer
  drift apart. The previously hand-edited `BuildMarker` string is replaced by
  `<version>+<revision>`, where CI supplies the commit SHA.
- Repo layout flattened to a single plugin: `plugins/LiftoffRaceBot/` →
  `src/JmtLiftoffMod/`.
- Documentation rewritten for one plugin — the LobbyBot comparison material is
  gone, and versioning/release process is documented in
  [docs/versioning.md](docs/versioning.md).

### Added

- GitHub Actions workflows: `ci` validates the version/tag/changelog wiring on
  every push, `release` publishes a GitHub release when a `v*` tag is pushed.

### Notes

No behavioural changes to telemetry, the track-control layer, or the server
protocol — the wire vocabulary in [`contracts/`](contracts/) is unchanged from
`LiftoffRaceBot`, other than the plugin name reported in `session_started`.

[1.2.0]: https://github.com/geekhostuk/jmt-liftoff-mod/releases/tag/v1.2.0
[1.1.0]: https://github.com/geekhostuk/jmt-liftoff-mod/releases/tag/v1.1.0
[1.0.0]: https://github.com/geekhostuk/jmt-liftoff-mod/releases/tag/v1.0.0
