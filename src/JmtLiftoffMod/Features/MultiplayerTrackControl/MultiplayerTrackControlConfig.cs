using BepInEx.Configuration;
using UnityEngine;

namespace JmtLiftoffMod.Features.MultiplayerTrackControl;

internal sealed class MultiplayerTrackControlConfig
{
    public ConfigEntry<bool> EnableMultiplayerTrackControl { get; }
    public ConfigEntry<bool> EnableDiscoveryLogging { get; }
    public ConfigEntry<bool> EnableDryRun { get; }
    public ConfigEntry<bool> AutoDumpOnLobbyJoin { get; }
    public ConfigEntry<bool> EnableHarmonyDiagnostics { get; }
    public ConfigEntry<bool> ShowDebugPanel { get; }
    public ConfigEntry<bool> EnableRawHotkeyFallback { get; }
    public ConfigEntry<bool> EnableCachedSetGameCallbackFallback { get; }
    public ConfigEntry<bool> DelayExecutorCommandClearUntilCompletion { get; }
    public ConfigEntry<bool> EnableChatDiagnostics { get; }
    public ConfigEntry<bool> AnnounceTrackChangesInChat { get; }
    public ConfigEntry<bool> LoopTargetSequence { get; }
    public ConfigEntry<KeyboardShortcut> TriggerDiscoveryHotkey { get; }
    public ConfigEntry<KeyboardShortcut> TriggerStateDumpHotkey { get; }
    public ConfigEntry<KeyboardShortcut> TriggerTrackChangeHotkey { get; }
    public ConfigEntry<KeyboardShortcut> TriggerCycleNextHotkey { get; }
    public ConfigEntry<bool> RequestDiscoveryDump { get; }
    public ConfigEntry<bool> RequestStateDump { get; }
    public ConfigEntry<bool> RequestTrackChange { get; }
    public ConfigEntry<bool> RequestCycleNext { get; }
    public ConfigEntry<bool> RequestDumpChatState { get; }
    public ConfigEntry<bool> RequestSendChatMessage { get; }
    public ConfigEntry<bool> RequestAdvanceSequence { get; }
    public ConfigEntry<string> TargetEnvironmentName { get; }
    public ConfigEntry<string> TargetTrackName { get; }
    public ConfigEntry<string> TargetRaceName { get; }
    public ConfigEntry<string> TargetWorkshopId { get; }
    public ConfigEntry<string> TargetSequence { get; }
    public ConfigEntry<string> PendingChatMessage { get; }
    public ConfigEntry<string> ChatMessageTemplate { get; }
    public ConfigEntry<bool> RequestDumpLobbyState { get; }
    public ConfigEntry<bool> RequestDumpMainMenu { get; }
    public ConfigEntry<bool> RequestCreateGame { get; }
    public ConfigEntry<bool> RequestNavigateToLobby { get; }
    public ConfigEntry<bool> EnableAutoRecovery { get; }
    public ConfigEntry<int> AutoRecoveryInitialDelaySecs { get; }

    public MultiplayerTrackControlConfig(ConfigFile config)
    {
        EnableMultiplayerTrackControl = config.Bind(
            "MultiplayerTrackControl",
            "EnableMultiplayerTrackControl",
            true,
            "Enable the experimental multiplayer host track/race control module.");

        EnableDiscoveryLogging = config.Bind(
            "MultiplayerTrackControl",
            "EnableDiscoveryLogging",
            true,
            "Enable detailed reflection discovery dumps for multiplayer settings flow.");

        EnableDryRun = config.Bind(
            "MultiplayerTrackControl",
            "EnableDryRun",
            false,
            "When enabled, track/race changes only log the flow without invoking the final apply step. " +
            "Defaults false in JmtLiftoffMod so the in-race executor actually applies host changes.");

        AutoDumpOnLobbyJoin = config.Bind(
            "MultiplayerTrackControl",
            "AutoDumpOnLobbyJoin",
            true,
            "Automatically dump multiplayer discovery and state when entering a multiplayer room.");

        EnableHarmonyDiagnostics = config.Bind(
            "MultiplayerTrackControl",
            "EnableHarmonyDiagnostics",
            true,
            "Patch likely multiplayer settings methods to log invocation order and arguments.");

        ShowDebugPanel = config.Bind(
            "MultiplayerTrackControl",
            "ShowDebugPanel",
            false,
            "Show a small IMGUI debug panel with multiplayer track control actions.");

        EnableRawHotkeyFallback = config.Bind(
            "MultiplayerTrackControl",
            "EnableRawHotkeyFallback",
            true,
            "Use direct Unity Input key polling as a fallback when KeyboardShortcut.IsDown does not fire.");

        EnableCachedSetGameCallbackFallback = config.Bind(
            "MultiplayerTrackControl",
            "EnableCachedSetGameCallbackFallback",
            false,
            "Unsafe fallback. When enabled, allows applying changes through a cached onSetGame delegate even if the popup is inactive. Disabled by default because it can leave Liftoff's game creation/settings flow in a bad state.");

        DelayExecutorCommandClearUntilCompletion = config.Bind(
            "MultiplayerTrackControl.Commands",
            "DelayExecutorCommandClearUntilCompletion",
            false,
            "When enabled, RequestTrackChange and RequestCycleNext stay set until the queued executor action reaches a terminal result, then auto-clear. Disabled by default to preserve immediate one-shot command semantics.");

        EnableChatDiagnostics = config.Bind(
            "MultiplayerTrackControl",
            "EnableChatDiagnostics",
            true,
            "Enable multiplayer chat discovery/state dumps and chat-related Harmony diagnostics when available.");

        AnnounceTrackChangesInChat = config.Bind(
            "MultiplayerTrackControl",
            "AnnounceTrackChangesInChat",
            false,
            "When enabled, send a chat message after a successful configured host track change if a send path can be resolved.");

        LoopTargetSequence = config.Bind(
            "MultiplayerTrackControl",
            "LoopTargetSequence",
            true,
            "When advancing a configured target sequence, loop back to the first entry after the last entry.");

        TriggerDiscoveryHotkey = config.Bind(
            "MultiplayerTrackControl.Hotkeys",
            "TriggerDiscoveryHotkey",
            new KeyboardShortcut(KeyCode.F6, KeyCode.LeftControl),
            "Hotkey to dump multiplayer discovery candidates.");

        TriggerStateDumpHotkey = config.Bind(
            "MultiplayerTrackControl.Hotkeys",
            "TriggerStateDumpHotkey",
            new KeyboardShortcut(KeyCode.F7, KeyCode.LeftControl),
            "Hotkey to dump current multiplayer and room state.");

        TriggerTrackChangeHotkey = config.Bind(
            "MultiplayerTrackControl.Hotkeys",
            "TriggerTrackChangeHotkey",
            new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl),
            "Hotkey to attempt a host-only track/race change using Liftoff's UI/settings flow.");

        TriggerCycleNextHotkey = config.Bind(
            "MultiplayerTrackControl.Hotkeys",
            "TriggerCycleNextHotkey",
            new KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl),
            "Hotkey to cycle to the next content candidate through the existing settings popup.");

        RequestDiscoveryDump = config.Bind(
            "MultiplayerTrackControl.Commands",
            "RequestDiscoveryDump",
            false,
            "One-shot command. Set true before launch or via config manager to dump discovery candidates once, then auto-reset.");

        RequestStateDump = config.Bind(
            "MultiplayerTrackControl.Commands",
            "RequestStateDump",
            false,
            "One-shot command. Set true before launch or via config manager to dump multiplayer state once, then auto-reset.");

        RequestTrackChange = config.Bind(
            "MultiplayerTrackControl.Commands",
            "RequestTrackChange",
            false,
            "One-shot command. Set true before launch or via config manager to attempt the configured track/race change once, then auto-reset.");

        RequestCycleNext = config.Bind(
            "MultiplayerTrackControl.Commands",
            "RequestCycleNext",
            false,
            "One-shot command. Set true before launch or via config manager to cycle to the next candidate once, then auto-reset.");

        RequestDumpChatState = config.Bind(
            "MultiplayerTrackControl.Commands",
            "RequestDumpChatState",
            false,
            "One-shot command. Set true to dump multiplayer chat state once, then auto-reset.");

        RequestSendChatMessage = config.Bind(
            "MultiplayerTrackControl.Commands",
            "RequestSendChatMessage",
            false,
            "One-shot command. Set true to send the configured PendingChatMessage once, then auto-reset.");

        RequestAdvanceSequence = config.Bind(
            "MultiplayerTrackControl.Commands",
            "RequestAdvanceSequence",
            false,
            "One-shot command. Set true to advance to the next target in TargetSequence and attempt the change once, then auto-reset.");

        RequestDumpLobbyState = config.Bind(
            "MultiplayerTrackControl.Commands",
            "RequestDumpLobbyState",
            false,
            "One-shot command. Set true to dump lobby-scene discovery (prefab holders, lobby panels, etc.), then auto-reset.");

        RequestDumpMainMenu = config.Bind(
            "MultiplayerTrackControl.Commands",
            "RequestDumpMainMenu",
            false,
            "One-shot command. Set true to dump main-menu discovery (create room buttons, Photon state, etc.), then auto-reset.");

        RequestCreateGame = config.Bind(
            "MultiplayerTrackControl.Commands",
            "RequestCreateGame",
            false,
            "One-shot command. Set true to attempt creating a multiplayer game from the main menu, then auto-reset.");

        RequestNavigateToLobby = config.Bind(
            "MultiplayerTrackControl.Commands",
            "RequestNavigateToLobby",
            false,
            "One-shot command. Set true to navigate to the multiplayer lobby screen, then auto-reset.");

        EnableAutoRecovery = config.Bind(
            "MultiplayerTrackControl",
            "EnableAutoRecovery",
            true,
            "Automatically recover after a Photon disconnect (ServerTimeout). " +
            "Dismisses the disconnect dialog, navigates to the multiplayer lobby, and creates a new game.");

        AutoRecoveryInitialDelaySecs = config.Bind(
            "MultiplayerTrackControl",
            "AutoRecoveryInitialDelaySecs",
            3,
            "Seconds to wait after a Photon disconnect before attempting to dismiss the disconnect dialog.");

        TargetEnvironmentName = config.Bind(
            "MultiplayerTrackControl.Targets",
            "TargetEnvironmentName",
            string.Empty,
            "Environment/map/level display name to target before selecting content. Example: Surtur or Hannover.");

        TargetTrackName = config.Bind(
            "MultiplayerTrackControl.Targets",
            "TargetTrackName",
            string.Empty,
            "Track display name to target when applying a host-side change.");

        TargetRaceName = config.Bind(
            "MultiplayerTrackControl.Targets",
            "TargetRaceName",
            string.Empty,
            "Race display name to target when applying a host-side change.");

        TargetWorkshopId = config.Bind(
            "MultiplayerTrackControl.Targets",
            "TargetWorkshopId",
            string.Empty,
            "Workshop/content identifier to match against LocalID or ManagedID when selecting content.");

        TargetSequence = config.Bind(
            "MultiplayerTrackControl.Targets",
            "TargetSequence",
            string.Empty,
            "Semicolon-separated rotation list. Entry format: Environment|Track|Race|WorkshopId. WorkshopId is optional.");

        PendingChatMessage = config.Bind(
            "MultiplayerTrackControl.Chat",
            "PendingChatMessage",
            string.Empty,
            "Outgoing chat message text used by RequestSendChatMessage.");

        ChatMessageTemplate = config.Bind(
            "MultiplayerTrackControl.Chat",
            "ChatMessageTemplate",
            "Switching to {environment} / {race}",
            "Template used when AnnounceTrackChangesInChat is enabled. Supported placeholders: {environment}, {track}, {race}, {workshop}.");
    }
}
