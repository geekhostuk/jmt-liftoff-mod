# Installing JMT Liftoff Mod and linking it to a server

## Prerequisites

- A Steam copy of **Liftoff: FPV Drone Racing** (Windows).
- **BepInEx 5.x (x64)** — the Unity Mono plugin loader.
- `JmtLiftoffMod.dll` — from a
  [release](https://github.com/geekhostuk/jmt-liftoff-mod/releases/latest) or built
  from source ([building.md](building.md)).
- A competition server to connect to, and an **API key** issued by that server
  for this bot.

The mod hosts the room it runs in, so give it **its own machine and game
instance** — don't run it on a client you also want to fly on.

## 1. Install BepInEx

1. Download the BepInEx 5.x x64 zip from
   [github.com/BepInEx/BepInEx/releases](https://github.com/BepInEx/BepInEx/releases).
2. Extract it into the Liftoff install folder (the one containing `Liftoff.exe`),
   typically `C:\Program Files (x86)\Steam\steamapps\common\Liftoff`.
3. Run Liftoff once and quit — BepInEx creates its folder structure
   (`BepInEx\plugins`, `BepInEx\config`, …).

## 2. Install the plugin

Copy the DLL into the plugins folder:

```
Liftoff\BepInEx\plugins\JmtLiftoffMod.dll
```

Run Liftoff once and quit. The plugin generates its config file:

```
Liftoff\BepInEx\config\uk.co.geekhost.jmtliftoffmod.cfg
```

## 3. Link it to your server

Open the generated `.cfg` in a text editor. It contains only the essentials:

```ini
[Competition]

## Enable the competition server connection.
Enabled = true

## WebSocket URL of the competition server.
ServerUrl = wss://your-server.example.com/ws/plugin

## API key sent in the Authorization header when connecting.
## Issued by the competition server for this bot.
ApiKey = <paste your bot's API key here>
```

- `ServerUrl` — your server's plugin WebSocket endpoint. Use `wss://` behind TLS
  in production, `ws://localhost:3000/ws/plugin` for local development.
- `ApiKey` — identifies this bot to the server; sent as
  `Authorization: Bearer <key>` on connect. **Treat it like a password** — do not
  commit configs containing real keys. (`*.cfg` is gitignored in this repo for
  exactly that reason.)
- `Enabled` defaults to `true` — set it to `false` only if you want the bot to run
  without a server connection.

Restart Liftoff and navigate the bot into a multiplayer room. In the BepInEx
console/log you should see:

```
[Competition] Client started. Target: wss://your-server.example.com/ws/plugin
[Competition] Connecting to wss://your-server.example.com/ws/plugin...
[Competition] Connected.
```

The server will receive a `session_started` event carrying the plugin name,
version and build marker, then `keepalive`s every minute — from there it can drive
the bot with the commands in [command-reference.md](command-reference.md).

## Upgrading from LiftoffRaceBot

This mod was previously published as `LiftoffRaceBot` in
[geekhostuk/liftoff-bots](https://github.com/geekhostuk/liftoff-bots). The GUID,
assembly name and config path all changed, so nothing carries over automatically:

| | Old | New |
|---|---|---|
| DLL | `LiftoffRaceBot.dll` | `JmtLiftoffMod.dll` |
| GUID | `uk.co.geekhost.liftoff.racebot` | `uk.co.geekhost.jmtliftoffmod` |
| Config | `…liftoff.racebot.cfg` | `…jmtliftoffmod.cfg` |
| Data/logs | `BepInEx\plugins\LiftoffRaceBot\` | `BepInEx\plugins\JmtLiftoffMod\` |

To upgrade: **delete `LiftoffRaceBot.dll`** (never run both — they would both
register Photon callbacks and fight over the room), drop in `JmtLiftoffMod.dll`,
start the game once to generate the new `.cfg`, then copy your `ServerUrl` and
`ApiKey` across from the old one. If your server identifies bots by the reported
plugin name, update it to expect `JMT Liftoff Mod`.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `[Competition] Disabled.` in the log | `Enabled = false` in the `.cfg`. |
| `No API key set — paste your bot's API key…` | `ApiKey` is empty. |
| `Disconnected: … Reconnecting in 5s...` looping | Server unreachable, wrong `ServerUrl`, or the server rejected the key. Check the server's logs. |
| Connected but no telemetry | The bot must be in a multiplayer room; most events only fire on Photon traffic. |
| No `gate_passed` events | The bot's own client has to be **loaded into the race scene**, not sitting in the waiting room. |
| Commands acked `skipped_stale` | The game's main thread hung for >30 s (e.g. loading); the server should re-issue. |

Plugin logs live in the BepInEx console and under
`BepInEx\plugins\JmtLiftoffMod\` (structured `.log` and JSONL files).
