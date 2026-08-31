# JMT Liftoff Mod

[![Release](https://img.shields.io/github/v/release/geekhostuk/jmt-liftoff-mod?label=release)](https://github.com/geekhostuk/jmt-liftoff-mod/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

The in-game half of the **JMT FPV platform** — a [BepInEx](https://github.com/BepInEx/BepInEx)
plugin that turns a copy of
[Liftoff: FPV Drone Racing](https://store.steampowered.com/app/410340/Liftoff_FPV_Drone_Racing/)
into a **remotely controlled multiplayer race host**.

This is the companion mod to the **JMT App**, which is not yet released. Liftoff
gives you a room and a track; everything a race organiser normally does by hand
around that — rotating tracks, timing pilots properly, keeping standings — has to
happen somewhere. The mod is the piece that lives inside the game and does it:

- **Automatic track control.** The platform decides what gets flown next and the
  mod applies it — environment, track, race, even Steam Workshop content — by
  driving Liftoff's own multiplayer UI. Nobody has to sit at the host machine
  clicking through menus between heats.
- **Better leaderboards.** Because the mod loads into the race scene as host, it
  sees every gate, not just the lap total: `gate_passed` and `sector_split` per
  pilot. That is enough for sector-by-sector comparisons and split-level pace
  analysis, rather than one number at the end of a lap.
- **Competitions.** Sessions, laps, gates and players stream to the platform as
  they happen, so standings, points and weekly competition state are computed
  from live race data instead of being typed in afterwards.

The mod sits in a game instance on dedicated hardware and is driven over a
persistent WebSocket connection — track rotation, race creation, chat
announcements, player kicks and lobby management are all server-commanded. While
it hosts the room it records race telemetry from Photon network traffic,
including **gate-by-gate timing** captured from inside the race scene.

The protocol between the two halves is documented and schema-defined in
[`contracts/`](contracts/), so the mod is not locked to the JMT App — any server
speaking the [documented protocol](docs/server-protocol.md) can drive it.

```
┌─────────────────────────────┐        WebSocket (wss)          ┌──────────────────────┐
│  Liftoff (Steam) + BepInEx  │   /ws/plugin                    │  JMT App (server)    │
│  ┌───────────────────────┐  │   Authorization: Bearer <key>   │  not in this repo    │
│  │  JMT Liftoff Mod      │◄─┼─────────────────────────────────┼─►                    │
│  │  • Photon callbacks   │  │  ── events ──────────────────►  │  • persists laps,    │
│  │  • lap/gate telemetry │  │  session_started, keepalive,    │    gates, players    │
│  │  • track control via  │  │  lap_recorded, gate_passed, …   │  • drives track      │
│  │    reflection into    │  │  ◄────────────────── commands ──│    rotation          │
│  │    Liftoff's UI flow  │  │  set_track, send_chat,          │  • admin dashboard   │
│  └───────────────────────┘  │  kick_player, create_game, …    │                      │
└─────────────────────────────┘                                 └──────────────────────┘
```

The plugin registers as a Photon callback target inside the game process, derives
race telemetry from Photon events (lap detection from event `200` and player-state
snapshots) and from a Harmony hook on `RaceCheckpoint.Trigger()`, and streams it
to the server as JSON events. The server sends JSON commands back; each command
carries a `command_id` and is acknowledged with a `command_ack` event. The full
message vocabulary is schema-defined in [`contracts/`](contracts/).

## Identity

| | |
|---|---|
| BepInPlugin GUID | `uk.co.geekhost.jmtliftoffmod` |
| Display name | JMT Liftoff Mod |
| Plugin DLL | `JmtLiftoffMod.dll` |
| Config file | `BepInEx\config\uk.co.geekhost.jmtliftoffmod.cfg` |
| Data/log folder | `BepInEx\plugins\JmtLiftoffMod\` |

## Quick start

1. Install [BepInEx 5.x (x64)](https://github.com/BepInEx/BepInEx/releases) into your
   Liftoff install folder and run the game once.
2. Download `JmtLiftoffMod.dll` from the
   [latest release](https://github.com/geekhostuk/jmt-liftoff-mod/releases/latest)
   and drop it into `Liftoff\BepInEx\plugins\`. (Or build it yourself —
   [docs/building.md](docs/building.md).)
3. Run Liftoff once more — the plugin writes its config to
   `BepInEx\config\uk.co.geekhost.jmtliftoffmod.cfg`.
4. Set `ServerUrl` and `ApiKey` in that config to point at the JMT App (or any
   server speaking the protocol) and restart the game. See
   [docs/install.md](docs/install.md).

## Documentation

| Doc | What it covers |
|---|---|
| [docs/architecture.md](docs/architecture.md) | Plugin skeleton, feature map, threading model, configuration philosophy |
| [docs/install.md](docs/install.md) | Installing BepInEx + the plugin and linking it to a server |
| [docs/building.md](docs/building.md) | Building from source (`LIFTOFF_DIR`, `BEPINEX_CORE_DIR`) |
| [docs/versioning.md](docs/versioning.md) | Where the version lives and how a release is cut |
| [docs/chat-capture.md](docs/chat-capture.md) | How chat is captured on receipt, and why the backlog is not replayed |
| [docs/server-protocol.md](docs/server-protocol.md) | WebSocket connection, auth, keepalive, command/ack correlation |
| [docs/command-reference.md](docs/command-reference.md) | Every server→plugin command and plugin→server event, with JSON examples |
| [docs/multiplayer-track-control.md](docs/multiplayer-track-control.md) | Reverse-engineering notes for the host-side track-control layer |
| [contracts/](contracts/) | JSON Schemas for every event and command |
| [CHANGELOG.md](CHANGELOG.md) | Release history |

## Status

The lobby telemetry/logging path is the stable part. The host track-control and
in-race-load path is reverse-engineered against Liftoff's own UI flow — after a
game update, validate in-game that the bot's client enters the race scene and that
`gate_passed` events resolve a non-null `actor` before relying on it. See
[docs/multiplayer-track-control.md](docs/multiplayer-track-control.md).

## History

This mod began as `LiftoffRaceBot` in
[geekhostuk/liftoff-bots](https://github.com/geekhostuk/liftoff-bots), a two-plugin
repo. It was extracted here as a standalone, versioned project and rebranded — the
plugin GUID, assembly name and config path all changed, so it does **not** pick up
an existing `uk.co.geekhost.liftoff.racebot.cfg`. Copy your `ServerUrl` and
`ApiKey` across when upgrading.

## What this repo is (and isn't)

- **In this repo:** the mod's source code, the message contracts, and the docs.
- **In each [release](https://github.com/geekhostuk/jmt-liftoff-mod/releases/latest):**
  a built `JmtLiftoffMod.dll` ready to drop into `BepInEx\plugins\`, plus a zip
  and `SHA256SUMS.txt`. You do not need to build the mod yourself to run it.
- **Not here at all:** the JMT App / competition server (a separate project), and
  Liftoff's and BepInEx's own assemblies.

No copyrighted game code or game DLLs are committed to this repository, and none
ever may be — so building **from source** requires your own Steam copy of Liftoff
([docs/building.md](docs/building.md)). That is also why the release binary is
built on a machine that owns the game rather than in CI
([docs/versioning.md](docs/versioning.md#release-assets)).

## License

[MIT](LICENSE). Liftoff is © LuGus Studios; this project is not affiliated with or
endorsed by LuGus Studios.
