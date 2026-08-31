# Multiplayer Track Control

## Current plugin architecture

The existing plugin is a single BepInEx `BaseUnityPlugin` in [JmtLiftoffMod.cs](../src/JmtLiftoffMod/JmtLiftoffMod.cs). It:

- registers itself as a Photon callback target via `PhotonNetwork.AddCallbackTarget`
- captures raw Photon events in `OnEvent`
- logs room/player property updates through `IInRoomCallbacks`
- derives lap/race telemetry from Photon event `200` and player property snapshots
- writes structured race output to `photon-race-*.log` and `photon-race-*.jsonl`

The new multiplayer track-control feature keeps that lap logging path untouched and hangs off the same plugin lifecycle.

## What was added

The new module lives under [Features/MultiplayerTrackControl](../src/JmtLiftoffMod/Features/MultiplayerTrackControl) and adds:

- config-driven enable/disable switches and hotkeys
- reflection discovery for likely multiplayer/game-settings/content types
- host-state detection using `CurrentContentContainer` when available plus Photon fallback
- Harmony diagnostics around Liftoff's multiplayer settings UI flow
- an experimental executor that tries to reuse Liftoff's own popup/controller path instead of forcing scene loads

## Static reverse-engineering anchors

These types were identified statically from `Assembly-CSharp.dll`:

- `Liftoff.Multiplayer.GameSetup.PopupQuickPlayMultiplayerSetup`
- `Liftoff.Multiplayer.GameSetup.ContentSettingsPanel`
- `Liftoff.Multiplayer.GameSetup.RoomSettingsPanel`
- `MultiplayerRaceScoreButtonPanel`
- `InGameMenuMainPanel`
- `CurrentContentContainer`

The most useful flow discovered so far is:

1. `MultiplayerRaceScoreButtonPanel.OnGameSettings()` or `InGameMenuMainPanel.OnMultiplayerGameSettings()` instantiates `PopupQuickPlayMultiplayerSetup`.
2. `PopupQuickPlayMultiplayerSetup.OnSetGame()` builds the updated multiplayer settings object.
3. The popup's `onSetGame` callback is handled by the surrounding UI/controller, which then pushes the update deeper into the game's own multiplayer systems.

## Testing

Config entries are written through BepInEx config under the `MultiplayerTrackControl` sections.

Default hotkeys:

- `Ctrl+F6`: dump discovery candidates
- `Ctrl+F7`: dump current multiplayer/session state
- `Ctrl+F8`: attempt configured track/race change
- `Ctrl+F10`: cycle to the next available content option

Key config values:

- `EnableMultiplayerTrackControl`
- `EnableDiscoveryLogging`
- `EnableDryRun`
- `EnableHarmonyDiagnostics`
- `ShowDebugPanel`
- `EnableRawHotkeyFallback`
- `EnableCachedSetGameCallbackFallback`
- `DelayExecutorCommandClearUntilCompletion`
- `EnableChatDiagnostics`
- `AnnounceTrackChangesInChat`
- `LoopTargetSequence`
- `TargetTrackName`
- `TargetRaceName`
- `TargetEnvironmentName`
- `TargetWorkshopId`
- `TargetSequence`
- `RequestDiscoveryDump`
- `RequestStateDump`
- `RequestTrackChange`
- `RequestCycleNext`
- `RequestDumpChatState`
- `RequestSendChatMessage`
- `RequestAdvanceSequence`
- `PendingChatMessage`
- `ChatMessageTemplate`

`TargetEnvironmentName` is the map/level/environment selector. Examples:

- `Surtur`
- `Hannover`

If a requested race is not available in the current environment, the executor now tries to walk the environment selector and find a matching content list before giving up.

If the game captures the hotkeys, the feature can also be triggered in two other ways:

- set `ShowDebugPanel = true` to get a small in-game IMGUI panel with buttons
- set one of the `Request*` config entries to `true` before launch; the plugin will consume it once, log the action, and reset it to `false`

`EnableCachedSetGameCallbackFallback` is intentionally disabled by default. It allows a more aggressive experimental path that can call a cached `onSetGame` delegate even when the popup instance is inactive. That path helped with reverse-engineering, but it can also leave Liftoff's multiplayer create/settings UI in a bad state, so normal testing should keep it off.

If the first apply attempt happens too early and Liftoff has not produced a live popup/controller yet, the service now defers the request and retries it for a short period instead of immediately failing or touching an inactive popup.

The executor now keeps a small in-memory pending-action queue for deferred host-setting changes. Repeated requests for the same action are deduplicated, and terminal summary logs now include the final status plus retry counts.

`DelayExecutorCommandClearUntilCompletion` is optional queue-friendly behavior for config-driven executor actions such as `RequestTrackChange`. When enabled, the config entry remains set while the action is pending and is only cleared after a terminal result, instead of being cleared immediately when first consumed.

`TargetSequence` adds a simple lobby rotation format:

- `Environment|Track|Race|WorkshopId;Environment|Track|Race|WorkshopId`

`WorkshopId` is optional. `RequestAdvanceSequence = true` is a one-shot "advance once" command: it compares the current room content to `TargetSequence`, selects the next entry, writes that entry into the normal `Target*` config fields, and then attempts the host-side change through the normal apply flow.

The plugin now also includes experimental chat discovery/send scaffolding. Binary inspection shows Liftoff ships multiplayer chat classes such as:

- `Liftoff.Multiplayer.Chat.ChatHistory`
- `Liftoff.Multiplayer.Chat.ChatWindowPanel`
- `Liftoff.Multiplayer.Chat.MessageEntryPanel`
- `Liftoff.Multiplayer.Chat.Messages.PlayerChatMessage`
- `Liftoff.Multiplayer.Chat.Messages.SystemChatMessage`

Current chat controls:

- `RequestDumpChatState = true` to log live chat objects and any readable message collections
- `PendingChatMessage` plus `RequestSendChatMessage = true` for a best-effort send attempt
- `AnnounceTrackChangesInChat = true` to try sending `ChatMessageTemplate` after a successful configured track change

Recent builds intentionally refuse to treat Unity's inherited `Component.SendMessage(...)` as a valid chat-send success. Chat send attempts now try to reveal the chat window first and only count declared chat-panel methods as real send candidates.

## Current limitations

- The executor currently prefers existing UI/controller instances and then invokes the popup flow by reflection. If Liftoff changes those type names or controller methods, discovery logs should show the new candidates first.
- Workshop ID matching is heuristic right now and checks `LocalID`, `ManagedID`, and race `TrackDependency` strings.
- Game-mode switching is still intentionally conservative. The content panel can fall back to `ClassicRace` internally even when the room snapshot is `InfiniteRace`, so logs should still be checked after cross-map changes.
- Cross-environment changes are now environment-aware, but the final validation step is still runtime-driven because Liftoff's popup can repopulate different content sets depending on current room state and internal filters.
- If Liftoff only exposes an inactive popup instance, the executor now aborts safely by default instead of forcing the cached direct-callback path.
- Chat sending is still best-effort and needs runtime validation against Liftoff's real `MessageEntryPanel` send method path.

## Next reverse-engineering steps

- Validate which controller method is actually responsible for applying host updates in the current Liftoff build and capture the exact room-property delta.
- Inspect the runtime session-settings object after a successful host update and map the obfuscated property names to track/race/environment fields.
- Add a stronger content lookup path for Workshop items if Liftoff exposes a stable managed-content registry at runtime.
