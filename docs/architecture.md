# Architecture

JMT Liftoff Mod is a single BepInEx plugin. Its entry point is one `Plugin` class:

```csharp
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin,
    IOnEventCallback,      // raw Photon events (incl. event 200 → lap detection)
    IInRoomCallbacks,      // player join/leave, room & player property changes
    IConnectionCallbacks   // connect/disconnect lifecycle
```

On `Awake()` the plugin:

1. Applies its Harmony patches (`harmony.PatchAll()`).
2. Binds configuration — essential keys to the real BepInEx `Config` (persisted to
   the user-facing `.cfg`), everything else to an in-memory `ConfigFile` created by
   `HiddenConfig.Create()` that is never written to disk.
3. Starts the main-thread liveness watchdog and raises Liftoff's host-inactivity
   kick threshold so the bot is never idle-kicked from its own room.
4. Constructs the feature services (below).
5. Registers itself with `PhotonNetwork.AddCallbackTarget(this)`.

Identity constants (`PluginGuid`, `PluginName`) are compiled in and not
configurable. `PluginVersion` and `BuildMarker` are **generated at build time**
from the single version in `Directory.Build.props` — see
[versioning.md](versioning.md). All four are reported to the server in the
`session_started` event, so the server always knows exactly which build is
connected.

## Feature map

| Folder | Key types | Purpose |
|---|---|---|
| `Features/Chat` | `ChatCaptureService` | Captures in-game chat messages and forwards them as `chat_message` events. |
| `Features/Competition` | `CompetitionClient`, `CompetitionConfig`, `SimpleJsonParser`, `CommandTimingContext` | The server link: persistent WebSocket, event outbox, command dispatch, acks. See [server-protocol.md](server-protocol.md). |
| `Features/Diagnostics` | `BotStatsOverlay`, `CompetitionStats`, `PhotonRpcNoiseSilencer` | Optional on-screen stats overlay and log-noise suppression. |
| `Features/Lobby` | `LobbyStatusService` | Emits `lobby_status` snapshots on change or on request. |
| `Features/Logging` | `FileLogWriter`, `ObjectDescriber` | Structured `.log` and JSONL output under the plugin folder, written on a background thread. |
| `Features/MultiplayerTrackControl` | `MultiplayerTrackControlService` + 11 supporting types | The host-control layer: discovers Liftoff's multiplayer setup UI (`PopupQuickPlayMultiplayerSetup`, content/room settings panels) **by reflection**, detects host state, and executes track/race/environment/workshop changes and game creation through the game's own UI flow. See [multiplayer-track-control.md](multiplayer-track-control.md). |
| `Features/Racing` | `CheckpointHookService`, `RaceEventEmitter` | Gate-by-gate timing via a Harmony postfix on `RaceCheckpoint.Trigger()` — emits `gate_passed` and `sector_split`. Only produces data once the client is loaded into a race scene. |

Because everything that touches game internals goes through reflection and
Harmony, the plugin survives minor game updates: lookups fail soft (logged, not
crashed) rather than breaking on renamed private members.

## Hosting behaviour

This mod is built to **run the game**, not just watch it. The track-control
executor runs live (`EnableDryRun` defaults to `false`), so:

- `create_game` opens Liftoff's create-game popup, confirms it, and hosts a real room;
- `set_track` applies environment / track / race — including Steam Workshop
  content — through the settings panels;
- the bot's own client loads into the race scene, which is what makes the
  checkpoint hook fire.

It is designed for **dedicated hardware**: one machine, one game instance, one bot
hosting one room, controlled entirely by the server.

### Lifecycle

1. **Awake** — Harmony patches, config binds, watchdog start, inactivity-kick
   disable, Photon callback registration.
2. **Connect** — WebSocket to the server, `session_started` with version + build marker.
3. **Hosting** — the server drives the room via `create_game` and `set_track`; gate
   telemetry streams while pilots fly.
4. **Between races** — `navigate_to_lobby` / `leave_lobby` reset state; keepalives
   carry room, track, player-count, host flag and main-thread health throughout.

## Threading model

- **Unity main thread** — all game/Photon API access, command execution, config.
- **CompetitionClient background task** — WebSocket I/O. Events are enqueued from
  the main thread into a `ConcurrentQueue` outbox; commands received from the
  server are posted back to the main thread via Unity's `SynchronizationContext`.
- **Log writer thread** — file I/O is kept off the main thread.
- **Watchdog thread** — tracks the last main-thread tick; the gap is reported in
  every `keepalive` as `main_thread_gap_ms` so the server can detect a hung game.
- Commands that act on game state (`set_track`, `next_track`, `send_chat`, …) are
  scheduled *staleable*: if the main thread was hung and the command is picked up
  more than 30 s after it was queued, it is skipped and acked `skipped_stale`
  instead of executing against a world that has moved on.

## Configuration philosophy

The user-facing `.cfg` contains **only what an operator must set per machine**:
`[Competition] Enabled`, `ServerUrl`, `ApiKey`.

Every other setting — Photon network tuning, log toggles, recording heuristics,
the entire track-control debug surface (hotkeys, one-shot commands, dry-run,
discovery dumps) — is bound to `HiddenConfig`, an in-memory `ConfigFile` that is
never persisted. The settings still behave like normal BepInEx config entries in
code (values, `SettingChanged`, one-shot resets), but changing a default requires
a rebuild. This keeps deployed configs tiny, self-explanatory, and impossible to
misconfigure into a broken state.

The important fixed defaults for this build: track control **enabled**, dry-run
**off**, checkpoint hook **on**, and the unsafe cached-`SetGame`-callback fallback
**off** (it can corrupt Liftoff's create/settings UI state).
