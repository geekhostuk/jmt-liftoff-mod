using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using JmtLiftoffMod.Features.Competition;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace JmtLiftoffMod.Features.MultiplayerTrackControl;

internal sealed class MultiplayerTrackControlService : IDisposable
{
    private readonly BaseUnityPlugin _plugin;
    private readonly string _pluginDir;
    private readonly Func<object?, string> _describe;
    private readonly MultiplayerTrackControlConfig _config;
    private readonly MultiplayerTrackControlLog _log;
    private readonly MultiplayerDiscoveryService _discovery;
    private readonly MultiplayerHostStateDetector _hostDetector;
    private readonly MultiplayerTrackChangeExecutor _executor;
    private readonly MultiplayerChatService _chatService;
    private readonly MultiplayerDiagnosticsPatches _patches;
    private static readonly TimeSpan PendingRetryDelay = TimeSpan.FromMilliseconds(500);
    private const int MaxPendingRetryAttempts = 12;

    private MultiplayerHostStateDetector.HostStateSnapshot? _lastSnapshot;
    private Rect _debugWindowRect = new(20f, 20f, 430f, 400f);
    private DateTime _lastPollUtc = DateTime.MinValue;
    private bool _hasLoggedTickSource;
    /// <summary>Shortest gap between two forced (Photon-callback) polls.</summary>
    private static readonly TimeSpan ForcedPollFloor = TimeSpan.FromMilliseconds(100);
    /// <summary>A forced poll the floor swallowed, retried from Update().</summary>
    private bool _activityPending;
    private string? _activitySource;
    private bool _initialized;
    private bool _wasInMultiplayer;
    private bool _wasHost;
    private readonly List<PendingExecutorAction> _pendingExecutorActions = new();
    private readonly HashSet<string> _activeConfigCommandKeys = new(StringComparer.OrdinalIgnoreCase);
    private string _lastActionStatus = string.Empty;
    private int _navStep;
    private int _navStepRetries;
    private const int MaxNavStepRetries = 10;
    private DateTime _navStepScheduledUtc = DateTime.MinValue;

    // ── Auto-recovery after Photon disconnect ────────────────────────────
    private int _recoveryStep;
    private int _recoveryStepRetries;
    private const int MaxRecoveryStepRetries = 5;
    private DateTime _recoveryScheduledUtc = DateTime.MinValue;
    private int _recoveryAttemptCount;
    private int _recoveryConnectAttempts;
    private int _step4CreateGameRetries;
    private const int MaxStep4CreateGameRetries = 4;
    private const int MaxRecoveryAttempts = 3;

    // Latest finalized status of the "create game" action (set by FinalizeExecutorAction).
    // Recovery Step 4 reads this to detect Failed/Aborted outcomes and retry quickly,
    // instead of waiting 30s in Step 5 for a room that's never coming.
    private MultiplayerTrackChangeExecutionStatus _lastCreateGameStatus = MultiplayerTrackChangeExecutionStatus.Applied;
    private DateTime _lastCreateGameStatusUtc = DateTime.MinValue;
    private DateTime _step4CreateGameFiredUtc = DateTime.MinValue;

    // ── Long-cooldown retry loop (was: hard cap at MaxRecoveryAttempts) ──
    // After three back-to-back recovery cycles fail, wait this long before
    // wiping the attempt counter and trying again. Logs make the wait visible.
    // Cap total recovery time at WallClockBudget — past that we still keep
    // trying but tag the alert as "stuck for hours" so the Discord watchdog
    // can escalate.
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RecoveryWallClockBudget = TimeSpan.FromHours(6);
    private DateTime _recoveryFirstFailureUtc = DateTime.MinValue;

    // ── Silent room-loss detection ───────────────────────────────────────
    // Fires when Photon quietly reconnects to Master without surfacing a
    // Disconnected callback (observed overnight on JMT-MEDIUM: room is gone
    // but no DisconnectCause was logged). We watch for "was in multiplayer,
    // now not, and Photon has been connected to Master for > N seconds" and
    // synthesise a recovery entry in that case.
    private DateTime _roomLossDetectedUtc = DateTime.MinValue;
    private static readonly TimeSpan SilentRoomLossGrace = TimeSpan.FromSeconds(10);

    public MultiplayerTrackControlService(
        BaseUnityPlugin plugin,
        string pluginDir,
        ManualLogSource logger,
        Action<string> stateLog,
        Func<object?, string> describe,
        Func<bool>? stateLogEnabled = null)
    {
        _plugin = plugin;
        _pluginDir = pluginDir;
        _describe = describe;
        // Track-control settings are all debug/diagnostic tuning — bind them to an in-memory
        // config so none of the MultiplayerTrackControl sections show up in the user-facing .cfg.
        _config = new MultiplayerTrackControlConfig(HiddenConfig.Create());
        _log = new MultiplayerTrackControlLog(logger, stateLog, stateLogEnabled);
        _discovery = new MultiplayerDiscoveryService(_log, describe);
        _hostDetector = new MultiplayerHostStateDetector(_discovery, _log, describe);
        _executor = new MultiplayerTrackChangeExecutor(_config, _discovery, _hostDetector, _log, describe);
        _chatService = new MultiplayerChatService(_config, _discovery, _hostDetector, _log, describe);
        _patches = new MultiplayerDiagnosticsPatches($"{Plugin.PluginGuid}.multiplayertrackcontrol", _discovery, _log, describe);
    }

    public void Initialize()
    {
        if (_initialized || !_config.EnableMultiplayerTrackControl.Value)
            return;

        _log.Info("INIT", $"Build marker={Plugin.BuildMarker}");
        _log.Info("INIT", $"Experimental multiplayer track control enabled. pluginDir={_pluginDir}");
        _log.Info("INIT", $"Hotkeys discovery={FormatShortcut(_config.TriggerDiscoveryHotkey.Value)} state={FormatShortcut(_config.TriggerStateDumpHotkey.Value)} change={FormatShortcut(_config.TriggerTrackChangeHotkey.Value)} cycle={FormatShortcut(_config.TriggerCycleNextHotkey.Value)}");
        _log.Info("INIT", $"Fallbacks rawInput={_config.EnableRawHotkeyFallback.Value} debugPanel={_config.ShowDebugPanel.Value} delayExecutorCommandClear={_config.DelayExecutorCommandClearUntilCompletion.Value} chatDiagnostics={_config.EnableChatDiagnostics.Value}");
        _log.Info("INIT", $"Targets env=\"{_config.TargetEnvironmentName.Value}\" track=\"{_config.TargetTrackName.Value}\" race=\"{_config.TargetRaceName.Value}\" workshop=\"{_config.TargetWorkshopId.Value}\"");
        _log.Info("INIT", $"Sequence loop={_config.LoopTargetSequence.Value} raw=\"{_config.TargetSequence.Value}\"");
        _discovery.Refresh();

        if (_config.EnableHarmonyDiagnostics.Value)
            _patches.Install();

        _initialized = true;
    }

    public void Update()
    {
        // A forced poll that was rate-limited away is retried here rather than
        // dropped, so a Photon state change is still acted on within the floor
        // rather than waiting for the next idle poll a second later.
        if (_activityPending)
        {
            Poll(_activitySource ?? "Update", force: true);
            return;
        }

        Poll("Update", force: false);
    }

    public void NotifyActivity(string source)
    {
        _activityPending = true;
        _activitySource = source;
        Poll(source, force: true);
    }

    // ── External API (called by CompetitionClient on the Unity main thread) ──

    public void ExternalCycleNext(string source)
    {
        if (!_initialized) return;
        _log.Info("API", $"External cycle-next requested from {source}");
        RunExecutorAction("cycle-next track/race", _executor.AttemptCycleNext, source);
    }

    public void ExternalSetTrack(string env, string track, string race, string workshopId, string source, CommandTimingContext? timing = null)
    {
        if (!_initialized) return;
        _log.Info("API", $"External set-track requested from {source}: env=\"{env}\" track=\"{track}\" race=\"{race}\" workshop=\"{workshopId}\"");
        timing?.StartPhase("config_update");
        _config.TargetEnvironmentName.Value = env;
        _config.TargetTrackName.Value = track;
        _config.TargetRaceName.Value = race;
        _config.TargetWorkshopId.Value = workshopId;
        RunExecutorAction("configured track/race change", () => _executor.AttemptConfiguredChange(timing), source);
    }

    // NOTE: ExternalPrepareTrack removed — warmup was discarded by set_track, adding load with no benefit.

    public bool ExternalTryCatalogSnapshot(out Dictionary<string, object?> catalog)
    {
        catalog = new Dictionary<string, object?>();
        if (!_initialized) return false;
        return _executor.TryCatalogSnapshot(out catalog);
    }

    public void ExternalUpdatePlaylist(string sequence, bool applyImmediately, string source)
    {
        if (!_initialized) return;
        _log.Info("API", $"External update-playlist requested from {source}: applyImmediately={applyImmediately} sequence=\"{sequence}\"");
        _config.TargetSequence.Value = sequence;
        if (applyImmediately)
            RunExecutorAction("cycle-next track/race", _executor.AttemptCycleNext, source);
    }

    public void ExternalSendChat(string message, string source)
    {
        if (!_initialized) return;
        _log.Info("API", $"External send-chat requested from {source}: \"{message}\"");
        _chatService.SendRaw(message);
    }

    public void ExternalCreateGame(string source)
    {
        if (!_initialized) return;
        _log.Info("API", $"External create-game requested from {source}");
        CancelRecovery("manual create-game command received");
        AttemptCreateGame(source);
    }

    public string GetScreenState()
    {
        if (!_initialized) return "unknown";
        var snapshot = _lastSnapshot ?? _hostDetector.Capture();
        if (snapshot.IsInMultiplayer)
            return snapshot.IsInLobbyWaitingRoom ? "in_room_lobby" : "in_room_game";
        if (snapshot.IsOnMultiplayerLobbyScreen)
            return "multiplayer_lobby";
        return "main_menu";
    }

    public void ExternalNavigateToLobby(string source)
    {
        if (!_initialized) return;
        _log.Info("API", $"External navigate-to-lobby requested from {source}");
        CancelRecovery("manual navigate-to-lobby command received");
        AttemptNavigateToMultiplayerLobby(source);
    }

    // ── Auto-recovery after Photon disconnect ────────────────────────────

    public void OnPhotonDisconnected(Photon.Realtime.DisconnectCause cause)
    {
        // Zombie popups referenced by MultiplayerRuntimeState survive a Photon
        // reconnect cycle. Their surface checks (active, has onSetGame callback)
        // still pass, but the underlying ContentSettingsPanel is half-destroyed,
        // and invoking OnSetGame on them wedges the main thread for minutes
        // until the next ServerTimeout. Force fresh popup discovery on the next
        // set_track by dropping the cached reference now, regardless of whether
        // auto-recovery is enabled or the cause is recoverable.
        MultiplayerRuntimeState.ClearLatestPopupObject();
        MultiplayerRuntimeState.ClearLatestPopupWithSetGameCallback();
        _log.Info("RECOVERY", $"Cleared cached popup references after disconnect cause={cause}");

        if (!_initialized || !_config.EnableAutoRecovery.Value)
            return;

        // Auto-recover from any connection-level disconnect. Observed causes:
        //   ServerTimeout        — peer lost the keepalive from the client (common).
        //   ClientTimeout        — client-side network stall (previously ignored; broke JMT-EASY).
        //   ExceptionOnConnect   — transient handshake failure.
        //   DisconnectByServerLogic / DisconnectByClientLogic — kicked by room logic.
        // Intentional leaves (DisconnectByClientLogic triggered by our own code
        // paths) are rare and still worth retrying — the room is gone either way.
        switch (cause)
        {
            case Photon.Realtime.DisconnectCause.ServerTimeout:
            case Photon.Realtime.DisconnectCause.ClientTimeout:
            case Photon.Realtime.DisconnectCause.ExceptionOnConnect:
            case Photon.Realtime.DisconnectCause.DisconnectByServerLogic:
            case Photon.Realtime.DisconnectCause.DisconnectByClientLogic:
                break;
            default:
                _log.Info("RECOVERY", $"Photon disconnect cause={cause} is not recoverable. Skipping auto-recovery.");
                return;
        }

        StartRecovery($"disconnect cause={cause}");
    }

    private void StartRecovery(string reason)
    {
        if (_recoveryStep > 0)
        {
            _log.Warn("RECOVERY", $"Recovery already in progress at step {_recoveryStep}. Ignoring new trigger ({reason}).");
            return;
        }

        _recoveryAttemptCount++;

        // Hit the per-cycle cap → enter long cooldown instead of giving up. Without
        // this, the bot stays wedged for hours overnight: even though the silent-
        // room-loss watchdog re-triggers StartRecovery, _recoveryAttemptCount was
        // never reset so we hit this same branch and bailed out forever.
        if (_recoveryAttemptCount > MaxRecoveryAttempts)
        {
            if (_recoveryFirstFailureUtc == DateTime.MinValue)
                _recoveryFirstFailureUtc = DateTime.UtcNow;

            var stuckFor = DateTime.UtcNow - _recoveryFirstFailureUtc;
            if (stuckFor > RecoveryWallClockBudget)
            {
                _log.Warn("RECOVERY", $"Stuck for {stuckFor.TotalHours:F1}h (over {RecoveryWallClockBudget.TotalHours}h budget) — Discord watchdog should escalate. Continuing to retry every {RecoveryCooldown.TotalMinutes:F0}min anyway.");
            }
            else
            {
                _log.Warn("RECOVERY", $"Cooldown for {RecoveryCooldown.TotalMinutes:F0}min before next attempt cycle (already failed {MaxRecoveryAttempts} in a row, total stuck for {stuckFor.TotalMinutes:F0}min). Trigger: {reason}.");
            }

            _recoveryAttemptCount = 0;
            _recoveryStep = 1;
            _recoveryStepRetries = 0;
            _recoveryConnectAttempts = 0;
            _step4CreateGameRetries = 0;
            _step4CreateGameFiredUtc = DateTime.MinValue;
            _recoveryScheduledUtc = DateTime.UtcNow + RecoveryCooldown;
            return;
        }

        var delaySecs = Math.Max(1, _config.AutoRecoveryInitialDelaySecs.Value);
        _log.Info("RECOVERY", $"Starting auto-recovery attempt {_recoveryAttemptCount}/{MaxRecoveryAttempts} ({reason}). Initial delay={delaySecs}s.");
        _recoveryStep = 1;
        _recoveryStepRetries = 0;
        _recoveryConnectAttempts = 0;
        _step4CreateGameRetries = 0;
        _step4CreateGameFiredUtc = DateTime.MinValue;
        _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(delaySecs);
    }

    private int _lastLoggedRecoveryStep;

    private void RunRecoveryStep(string source)
    {
        if (_recoveryStep != _lastLoggedRecoveryStep)
        {
            _log.Info("RECOVERY", $"Running recovery step {_recoveryStep} from {source} (attempt {_recoveryAttemptCount}/{MaxRecoveryAttempts})");
            _lastLoggedRecoveryStep = _recoveryStep;
        }

        switch (_recoveryStep)
        {
            case 1:
                // Step 1: Dismiss the disconnect dialog by clicking OK/Close
                _recoveryStepRetries++;
                if (DismissAnyActiveDialog())
                {
                    _log.Info("RECOVERY", "Step 1: Disconnect dialog dismissed. Waiting 8s for scene transition...");
                    _recoveryStep = 2;
                    _recoveryStepRetries = 0;
                    _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(8);
                }
                else if (_recoveryStepRetries >= MaxRecoveryStepRetries)
                {
                    _log.Warn("RECOVERY", "Step 1: Could not find disconnect dialog after retries. Proceeding anyway (may already be on main menu).");
                    _recoveryStep = 2;
                    _recoveryStepRetries = 0;
                    _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                }
                else
                {
                    if (_recoveryStepRetries == 1 || _recoveryStepRetries >= MaxRecoveryStepRetries - 1)
                        _log.Info("RECOVERY", $"Step 1: Looking for disconnect dialog... ({_recoveryStepRetries}/{MaxRecoveryStepRetries})");
                    _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
                }
                break;

            case 2:
                // Step 2: Handle sign-in screen by clicking Connect (skips Liftoff Pro auth)
                // Try up to 7 times (7s) since the sign-in screen may take a moment to appear
                _recoveryStepRetries++;
                if (TryClickConnectOnSignInScreen())
                {
                    _log.Info("RECOVERY", "Step 2: Connect button clicked. Waiting 7s for connection...");
                    _recoveryStep = 3;
                    _recoveryStepRetries = 0;
                    _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(7);
                }
                else if (_recoveryStepRetries >= 7)
                {
                    _log.Info("RECOVERY", "Step 2: No sign-in screen detected after 7 checks. Proceeding to navigation.");
                    _recoveryStep = 3;
                    _recoveryStepRetries = 0;
                    _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }
                else
                {
                    if (_recoveryStepRetries == 1 || _recoveryStepRetries >= 6)
                        _log.Info("RECOVERY", $"Step 2: Checking for sign-in screen (to click Connect)... ({_recoveryStepRetries}/7)");
                    _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }
                break;

            case 3:
                // Step 3: Navigate to multiplayer lobby
                if (TryHandleSignInScreenIfPresent(3)) return;
                var snapshot = _hostDetector.Capture();
                if (snapshot.IsInMultiplayer)
                {
                    _log.Info("RECOVERY", "Step 3: Already in a multiplayer room. Recovery complete.");
                    CompleteRecovery();
                    return;
                }
                if (snapshot.IsOnMultiplayerLobbyScreen)
                {
                    _log.Info("RECOVERY", "Step 3: Already on multiplayer lobby screen. Skipping to create game.");
                    _recoveryStep = 4;
                    _recoveryStepRetries = 0;
                    _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                    return;
                }
                _log.Info("RECOVERY", "Step 3: Navigating to multiplayer lobby...");
                AttemptNavigateToMultiplayerLobby("auto-recovery");
                _recoveryStep = 4;
                _recoveryStepRetries = 0;
                // The nav step machine runs inside Poll(). Give it time for its 3 sub-steps.
                _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                break;

            case 4:
                // Step 4: Wait for lobby screen, fire create-game, then watch the action's
                // outcome. If create-game finalises with anything other than Applied (e.g.
                // zombie popup → Deferred → action queue gives up → Failed), retry Step 4
                // up to MaxStep4CreateGameRetries times with a 5s gap (and a fresh discovery
                // refresh) before falling through to Step 5's 30s room-join check.
                if (TryHandleSignInScreenIfPresent(4)) return;
                var snap = _hostDetector.Capture();
                if (snap.IsInMultiplayer)
                {
                    _log.Info("RECOVERY", "Step 4: Already in a multiplayer room. Recovery complete.");
                    CompleteRecovery();
                    return;
                }

                // Already fired create-game — watching for outcome.
                if (_step4CreateGameFiredUtc != DateTime.MinValue)
                {
                    var actionPending = FindPendingAction("create game") != null;
                    var sinceFire = DateTime.UtcNow - _step4CreateGameFiredUtc;
                    var hasFreshOutcome = _lastCreateGameStatusUtc > _step4CreateGameFiredUtc;

                    if (hasFreshOutcome && _lastCreateGameStatus == MultiplayerTrackChangeExecutionStatus.Applied)
                    {
                        _log.Info("RECOVERY", "Step 4: create-game returned Applied. Watching for room join in Step 5.");
                        _recoveryStep = 5;
                        _recoveryStepRetries = 0;
                        _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                        return;
                    }

                    if (hasFreshOutcome && !actionPending)
                    {
                        // Action finalised with non-Applied status — retry quickly.
                        _step4CreateGameRetries++;
                        if (_step4CreateGameRetries < MaxStep4CreateGameRetries)
                        {
                            _log.Warn("RECOVERY", $"Step 4: create-game finalised status={_lastCreateGameStatus}. Retry {_step4CreateGameRetries}/{MaxStep4CreateGameRetries} in 3s.");
                            _step4CreateGameFiredUtc = DateTime.MinValue;
                            _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                            return;
                        }

                        _log.Warn("RECOVERY", $"Step 4: create-game keeps failing (status={_lastCreateGameStatus}, {_step4CreateGameRetries} retries). Falling to Step 5 to wait for any external recovery.");
                        _recoveryStep = 5;
                        _recoveryStepRetries = 0;
                        _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                        return;
                    }

                    // Action still queued — give the action queue room to retry (12 attempts × 500ms = 6s).
                    if (sinceFire < TimeSpan.FromSeconds(12))
                    {
                        _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                        return;
                    }

                    // Stuck queued past expected window — retry Step 4.
                    _step4CreateGameRetries++;
                    if (_step4CreateGameRetries < MaxStep4CreateGameRetries)
                    {
                        _log.Warn("RECOVERY", $"Step 4: create-game still pending after {sinceFire.TotalSeconds:F0}s. Retry {_step4CreateGameRetries}/{MaxStep4CreateGameRetries} in 3s.");
                        _step4CreateGameFiredUtc = DateTime.MinValue;
                        _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                        return;
                    }

                    _log.Warn("RECOVERY", "Step 4: create-game keeps stalling. Falling to Step 5.");
                    _recoveryStep = 5;
                    _recoveryStepRetries = 0;
                    _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                    return;
                }

                // Have not fired create-game yet — fire it now if we are on the lobby screen
                // (or if navigation reported done but state probe hasn't caught up yet).
                if (snap.IsOnMultiplayerLobbyScreen || _navStep == 0)
                {
                    _log.Info("RECOVERY", "Step 4: Creating game...");
                    _discovery.Refresh();
                    AttemptCreateGame("auto-recovery");
                    _step4CreateGameFiredUtc = DateTime.UtcNow;
                    _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                    return;
                }

                _recoveryStepRetries++;
                if (_recoveryStepRetries >= MaxRecoveryStepRetries * 2)
                {
                    _log.Warn("RECOVERY", "Step 4: Timed out waiting for lobby screen. Attempting create game anyway.");
                    _discovery.Refresh();
                    AttemptCreateGame("auto-recovery");
                    _step4CreateGameFiredUtc = DateTime.UtcNow;
                    _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                }
                else
                {
                    if (_recoveryStepRetries == 1 || _recoveryStepRetries % 5 == 0 || _recoveryStepRetries >= MaxRecoveryStepRetries * 2 - 1)
                        _log.Info("RECOVERY", $"Step 4: Waiting for lobby screen... navStep={_navStep} ({_recoveryStepRetries}/{MaxRecoveryStepRetries * 2})");
                    _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }
                break;

            case 5:
                // Step 5: Verify we're back in a room (poll every 2s, up to 30s total)
                if (TryHandleSignInScreenIfPresent(5)) return;
                _recoveryStepRetries++;
                var finalSnap = _hostDetector.Capture();
                if (finalSnap.IsInMultiplayer)
                {
                    _log.Info("RECOVERY", "Step 5: Back in a multiplayer room. Emitting lobby status for server sync.");
                    CompleteRecovery();
                }
                else if (_recoveryStepRetries >= 15)
                {
                    _log.Warn("RECOVERY", $"Step 5: Not back in room after {_recoveryStepRetries} checks (~30s). Recovery attempt {_recoveryAttemptCount}/{MaxRecoveryAttempts} failed.");
                    _recoveryStepRetries = 0;
                    _step4CreateGameRetries = 0;
                    _step4CreateGameFiredUtc = DateTime.MinValue;

                    if (_recoveryAttemptCount < MaxRecoveryAttempts)
                    {
                        _recoveryAttemptCount++;
                        var backoffSecs = Math.Min(5 * _recoveryAttemptCount, 30);
                        _log.Info("RECOVERY", $"Restarting recovery from Step 1. Attempt {_recoveryAttemptCount}/{MaxRecoveryAttempts} in {backoffSecs}s.");
                        _recoveryStep = 1;
                        _recoveryConnectAttempts = 0;
                        _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(backoffSecs);
                    }
                    else
                    {
                        // Don't stop — enter long cooldown then restart cycle (was: "Manual intervention required").
                        if (_recoveryFirstFailureUtc == DateTime.MinValue)
                            _recoveryFirstFailureUtc = DateTime.UtcNow;
                        var stuckFor = DateTime.UtcNow - _recoveryFirstFailureUtc;
                        if (stuckFor > RecoveryWallClockBudget)
                            _log.Warn("RECOVERY", $"Stuck for {stuckFor.TotalHours:F1}h (over {RecoveryWallClockBudget.TotalHours}h budget). Continuing to retry every {RecoveryCooldown.TotalMinutes:F0}min.");
                        else
                            _log.Warn("RECOVERY", $"All {MaxRecoveryAttempts} recovery attempts in this cycle exhausted. Cooldown for {RecoveryCooldown.TotalMinutes:F0}min then restarting from Step 1 (total stuck for {stuckFor.TotalMinutes:F0}min).");
                        _recoveryAttemptCount = 0;
                        _recoveryStep = 1;
                        _recoveryConnectAttempts = 0;
                        _recoveryScheduledUtc = DateTime.UtcNow + RecoveryCooldown;
                    }
                }
                else
                {
                    if (_recoveryStepRetries == 1 || _recoveryStepRetries % 5 == 0 || _recoveryStepRetries >= 14)
                        _log.Info("RECOVERY", $"Step 5: Waiting for room join... ({_recoveryStepRetries}/15)");
                    _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                }
                break;

            default:
                _recoveryStep = 0;
                break;
        }
    }

    private bool DismissAnyActiveDialog()
    {
        var allButtons = UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Button>();
        // Don't include "back" — when Step 1 retries after a failed cycle the bot is
        // typically already on the lobby (no real dialog), and matching the lobby's
        // own buttonBack throws us back to the main menu, lengthening every retry.
        // The Liftoff disconnect dialog always has an OK button.
        var dialogHints = new[] { "ok", "close", "confirm", "continue", "accept" };

        foreach (var hint in dialogHints)
        {
            foreach (var button in allButtons)
            {
                if (button == null || !button.gameObject.activeInHierarchy) continue;

                var label = GetButtonLabelText(button);
                if (label != null && label.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _log.Info("RECOVERY", $"Dismissing dialog: clicking button label=\"{label}\" GO=\"{button.gameObject.name}\" hint=\"{hint}\"");
                    button.onClick.Invoke();
                    return true;
                }
            }
        }

        _log.Info("RECOVERY", $"No dialog button found among {allButtons.Length} active buttons.");
        return false;
    }

    private bool TryClickConnectOnSignInScreen()
    {
        // Only act when the sign-in screen is actually present. We detect it by finding
        // active SignInForm MonoBehaviour instances. We search by the signInCredentialsButton
        // field name to discover the type, but we do NOT require that button to be active —
        // it lives in a panel that is hidden when anonymous login is shown (which is the case
        // for bot accounts that bypass Liftoff Pro). Checking isActiveAndEnabled on the
        // MonoBehaviour itself is the correct presence test.
        var signInFormTypes = _discovery.FindTypesByFieldName("signInCredentialsButton");
        var liveSignInForms = new List<(Type type, object instance)>();
        foreach (var type in signInFormTypes)
        {
            foreach (var instance in ReflectionHelper.GetLiveObjects(type))
            {
                if (instance is not Behaviour behaviour || !behaviour.isActiveAndEnabled)
                    continue;
                liveSignInForms.Add((type, instance));
                break;
            }
        }

        if (liveSignInForms.Count == 0)
        {
            // SignInForm not detected via reflection (type not yet in discovery cache or
            // no live active instance). Fall through to the button scan below only when
            // we're not on a multiplayer screen — prevents accidentally clicking an
            // anonymous/connect button in the lobby during Steps 3-5.
            var fallbackSnap = _hostDetector.Capture();
            if (fallbackSnap.IsOnMultiplayerLobbyScreen || fallbackSnap.IsInMultiplayer)
                return false;
            _log.Info("RECOVERY", "SignInForm not found via reflection — attempting button scan (not on lobby/multiplayer).");
        }

        // 1) Reflection path: look for a button field on the SignInForm whose name contains
        // "anonymous" (signInAnonymousButton — the bypass-Pro button) or "connect" as a
        // fallback for any future Liftoff UI refactors.
        foreach (var (type, instance) in liveSignInForms)
        {
            var buttonFields = type.GetFields(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            foreach (var f in buttonFields)
            {
                if (!typeof(UnityEngine.UI.Button).IsAssignableFrom(f.FieldType)) continue;
                if (f.Name.IndexOf("anonymous", StringComparison.OrdinalIgnoreCase) < 0 &&
                    f.Name.IndexOf("connect", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var button = f.GetValue(instance) as UnityEngine.UI.Button;
                if (button != null && button.gameObject.activeInHierarchy)
                {
                    _log.Info("RECOVERY", $"Found anonymous/connect button via reflection: field=\"{f.Name}\" GO=\"{button.gameObject.name}\" on {type.FullName}");
                    button.onClick.Invoke();
                    return true;
                }
            }
        }

        // 2) Scan path: find an active Button whose label or GameObject name contains
        // "connect", "anonymous", or "guest" (case-insensitive) and click it.
        var allButtons = UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Button>();
        foreach (var button in allButtons)
        {
            if (button == null || !button.gameObject.activeInHierarchy) continue;

            var label = GetButtonLabelText(button);
            if (label != null && (
                    label.IndexOf("connect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    label.IndexOf("anonymous", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    label.IndexOf("guest", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                _log.Info("RECOVERY", $"Found anonymous/connect button by label: label=\"{label}\" GO=\"{button.gameObject.name}\"");
                button.onClick.Invoke();
                return true;
            }

            var goName = button.gameObject.name;
            if (goName != null && (
                    goName.IndexOf("connect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    goName.IndexOf("anonymous", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    goName.IndexOf("guest", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                _log.Info("RECOVERY", $"Found anonymous/connect button by GO name: GO=\"{goName}\" label=\"{label}\"");
                button.onClick.Invoke();
                return true;
            }
        }

        // Log all active button labels/names to help diagnose what screen is actually showing.
        var buttonDump = string.Join(", ", allButtons
            .Where(b => b != null && b.gameObject.activeInHierarchy)
            .Select(b => $"\"{GetButtonLabelText(b) ?? "(no label)"}\"/{b.gameObject.name}")
            .Take(30));
        if (liveSignInForms.Count > 0)
            _log.Warn("RECOVERY", $"Sign-in screen found ({liveSignInForms.Count} SignInForm instance(s)) but no anonymous/connect button visible on it (scanned {allButtons.Length} buttons). Active buttons: [{buttonDump}]");
        else
            _log.Warn("RECOVERY", $"No sign-in screen detected (0 active SignInForm instances) — scanned {allButtons.Length} buttons, none matched. Active buttons: [{buttonDump}]");
        return false;
    }

    private void CompleteRecovery()
    {
        _log.Info("RECOVERY", $"Recovery attempt {_recoveryAttemptCount} succeeded.");
        _recoveryStep = 0;
        _recoveryStepRetries = 0;
        _recoveryAttemptCount = 0;
        _recoveryConnectAttempts = 0;
        _step4CreateGameRetries = 0;
        _step4CreateGameFiredUtc = DateTime.MinValue;
        _recoveryFirstFailureUtc = DateTime.MinValue;
        _executor.ClearZombieBlacklist();
        if (_pendingExecutorActions.Count > 0)
            CancelPendingExecutorActions("recovery completed");
    }

    /// <summary>
    /// Checks for a sign-in screen and clicks Connect if found (skips Liftoff Pro auth).
    /// Returns true if the sign-in screen was detected and handled (caller should return and
    /// let the recovery scheduler retry the current step after the connect flow completes).
    /// </summary>
    private bool TryHandleSignInScreenIfPresent(int currentStep)
    {
        if (_recoveryConnectAttempts >= 3) return false;
        if (!TryClickConnectOnSignInScreen()) return false;

        _recoveryConnectAttempts++;
        _log.Info("RECOVERY", $"Step {currentStep}: Sign-in screen re-appeared. Clicked Connect ({_recoveryConnectAttempts}/3). Waiting 10s...");
        _recoveryScheduledUtc = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        return true;
    }

    private void CancelRecovery(string reason)
    {
        if (_recoveryStep == 0) return;
        _log.Info("RECOVERY", $"Recovery cancelled: {reason}");
        _recoveryStep = 0;
        _recoveryStepRetries = 0;
        if (_pendingExecutorActions.Count > 0)
            CancelPendingExecutorActions($"recovery cancelled: {reason}");
    }

    private void AttemptNavigateToMultiplayerLobby(string source)
    {
        _log.Info("NAV", $"Attempting to navigate to multiplayer lobby from {source}");

        var snapshot = _hostDetector.Capture();
        if (snapshot.IsOnMultiplayerLobbyScreen)
        {
            _lastActionStatus = "Already on multiplayer lobby screen.";
            _log.Info("NAV", _lastActionStatus);
            _navStep = 0;
            return;
        }

        if (snapshot.IsInMultiplayer)
        {
            _lastActionStatus = "Cannot navigate — already in a multiplayer room. Leave first.";
            _log.Warn("NAV", _lastActionStatus);
            _navStep = 0;
            return;
        }

        // Start multi-step navigation: MULTIPLAYER → Lobby → Play
        _navStep = 1;
        _navStepRetries = 0;
        _navStepScheduledUtc = DateTime.MinValue;
        RunNavStep(source);
    }

    private void RunNavStep(string source)
    {
        _log.Info("NAV", $"Running nav step {_navStep} from {source}");

        switch (_navStep)
        {
            case 1:
                // Step 1: Click the MULTIPLAYER heading on the main menu
                var (mpButton, _) = _discovery.FindMainMenuMultiplayerButton();
                if (mpButton != null)
                {
                    _log.Info("NAV", $"Step 1: Clicking MULTIPLAYER button: {mpButton.gameObject.name}");
                    mpButton.onClick.Invoke();
                    _lastActionStatus = "Step 1/3: MULTIPLAYER clicked. Waiting for submenu...";
                    _navStep = 2;
                    _navStepScheduledUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);
                }
                else
                {
                    _lastActionStatus = "Could not find MULTIPLAYER button. Navigate manually.";
                    _log.Warn("NAV", _lastActionStatus);
                    _navStep = 0;
                }
                break;

            case 2:
                // Step 2: Click the Lobby button in the multiplayer submenu
                _navStepRetries++;
                if (TryClickButtonByExactName("MultiplayerLobby", out var lobbyName) ||
                    TryClickButtonByLabelOrName("lobby", out lobbyName))
                {
                    _lastActionStatus = $"Step 2/3: '{lobbyName}' clicked. Waiting for popup...";
                    _navStep = 3;
                    _navStepRetries = 0;
                    _navStepScheduledUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(1200);
                }
                else if (_navStepRetries >= MaxNavStepRetries)
                {
                    _lastActionStatus = "Step 2: Could not find Lobby button after retries. Click it manually.";
                    _log.Warn("NAV", _lastActionStatus);
                    _navStep = 0;
                }
                else
                {
                    _lastActionStatus = $"Step 2: Looking for Lobby button... ({_navStepRetries}/{MaxNavStepRetries})";
                    _navStepScheduledUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
                }
                break;

            case 3:
                // Step 3: Click the Play/Enter button on the multiplayer lobby popup
                // Known button: btnMultiplayerLobby (no label, GO name contains "MultiplayerLobby")
                _navStepRetries++;

                // Check if we're already on the lobby screen
                var snap = _hostDetector.Capture();
                if (snap.IsOnMultiplayerLobbyScreen)
                {
                    _lastActionStatus = "Arrived at multiplayer lobby screen.";
                    _log.Info("NAV", _lastActionStatus);
                    _navStep = 0;
                    return;
                }

                // Look specifically for btnMultiplayerLobby (the play button on the popup)
                if (TryClickButtonByExactName("btnMultiplayerLobby", out var exactName) ||
                    TryClickButtonByLabelOrName("play", out exactName) ||
                    TryClickButtonByLabelOrName("join", out exactName) ||
                    TryClickButtonByLabelOrName("connect", out exactName) ||
                    TryClickButtonByLabelOrName("enter", out exactName))
                {
                    _lastActionStatus = $"Step 3/3: '{exactName}' clicked. Lobby loading...";
                    _navStep = 0;
                }
                else if (_navStepRetries >= MaxNavStepRetries)
                {
                    _lastActionStatus = "Step 3: Could not find Play button after retries. Click it manually.";
                    _log.Warn("NAV", _lastActionStatus);
                    _navStep = 0;
                }
                else
                {
                    _lastActionStatus = $"Step 3: Looking for Play button... ({_navStepRetries}/{MaxNavStepRetries})";
                    _navStepScheduledUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
                }
                break;

            default:
                _navStep = 0;
                break;
        }
    }

    private bool TryClickButtonByExactName(string exactGoName, out string matchedName)
    {
        matchedName = string.Empty;
        var allButtons = UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Button>();
        foreach (var button in allButtons)
        {
            if (button == null || !button.gameObject.activeInHierarchy) continue;
            if (string.Equals(button.gameObject.name, exactGoName, StringComparison.Ordinal))
            {
                _log.Info("NAV", $"Clicking button by exact name '{exactGoName}': {ReflectionHelper.DescribeObjectIdentity(button)}");
                matchedName = exactGoName;
                button.onClick.Invoke();
                return true;
            }
        }
        return false;
    }

    private bool TryClickButtonByLabelOrName(string hint, out string matchedName)
    {
        matchedName = string.Empty;
        var allButtons = UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Button>();
        foreach (var button in allButtons)
        {
            if (button == null || !button.gameObject.activeInHierarchy) continue;

            // Check label text
            var label = GetButtonLabelText(button);
            if (label != null && label.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _log.Info("NAV", $"Clicking button by label '{label}' (hint='{hint}'): {button.gameObject.name}");
                matchedName = label;
                button.onClick.Invoke();
                return true;
            }

            // Check GO name
            var goName = button.gameObject.name;
            if (goName != null && goName.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _log.Info("NAV", $"Clicking button by GO name '{goName}' (hint='{hint}')");
                matchedName = goName;
                button.onClick.Invoke();
                return true;
            }
        }

        _log.Warn("NAV", $"No button found for hint '{hint}' among {allButtons.Length} buttons.");
        return false;
    }

    private static string? GetButtonLabelText(UnityEngine.UI.Button button)
    {
        var text = button.GetComponentInChildren<UnityEngine.UI.Text>(includeInactive: false);
        if (text != null && !string.IsNullOrWhiteSpace(text.text))
            return text.text;

        // Try TextMeshPro via reflection
        var components = button.GetComponentsInChildren<Component>(includeInactive: false);
        foreach (var comp in components)
        {
            if (comp == null) continue;
            var typeName = comp.GetType().Name;
            if (typeName.Contains("TextMeshPro") || typeName.Contains("TMP_Text"))
            {
                var textProp = comp.GetType().GetProperty("text", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                var val = textProp?.GetValue(comp) as string;
                if (!string.IsNullOrWhiteSpace(val))
                    return val;
            }
        }

        return null;
    }

    private void AttemptCreateGame(string source)
    {
        _log.Info("CREATE_GAME", $"Attempting to create game from {source}");

        var snapshot = _hostDetector.Capture();

        // Phase 1: Check if already in a room
        if (PhotonNetwork.InRoom)
        {
            _lastActionStatus = "Already in a Photon room. Use track change to modify settings.";
            _log.Warn("CREATE_GAME", _lastActionStatus);
            return;
        }

        // Phase 2: Ensure we're on the lobby screen
        if (!snapshot.IsOnMultiplayerLobbyScreen)
        {
            _log.Info("CREATE_GAME", "Not on lobby screen. Attempting navigation first...");
            AttemptNavigateToMultiplayerLobby(source + "/auto-nav");

            // Re-check after navigation attempt
            snapshot = _hostDetector.Capture();
            if (!snapshot.IsOnMultiplayerLobbyScreen)
            {
                _lastActionStatus = "Not on multiplayer lobby screen. Navigate there first, then retry.";
                _log.Warn("CREATE_GAME", _lastActionStatus);
                return;
            }
        }

        // Phase 3: Click buttonCreateRoom to spawn the game setup popup
        var createRoomTypes = _discovery.FindTypesByFieldName("buttonCreateRoom");
        if (createRoomTypes.Count == 0)
        {
            _lastActionStatus = "No type with 'buttonCreateRoom' found.";
            _log.Warn("CREATE_GAME", _lastActionStatus);
            return;
        }

        bool buttonClicked = false;
        foreach (var type in createRoomTypes)
        {
            var liveObjects = ReflectionHelper.GetLiveObjects(type);
            if (liveObjects.Count == 0)
            {
                _log.Warn("CREATE_GAME", $"Type {type.FullName} found but no live instances.");
                continue;
            }

            foreach (var instance in liveObjects)
            {
                _log.Info("CREATE_GAME", $"Found live lobby panel: {ReflectionHelper.DescribeObjectIdentity(instance)}");

                var buttonField = type.GetField("buttonCreateRoom",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (buttonField == null)
                {
                    _log.Warn("CREATE_GAME", "buttonCreateRoom field not found via reflection.");
                    continue;
                }

                var button = buttonField.GetValue(instance) as UnityEngine.UI.Button;
                if (button == null)
                {
                    _log.Warn("CREATE_GAME", "buttonCreateRoom field value is null or not a Button.");
                    continue;
                }

                _log.Info("CREATE_GAME", $"Invoking buttonCreateRoom.onClick on {ReflectionHelper.DescribeObjectIdentity(instance)}");
                button.onClick.Invoke();
                _log.Info("CREATE_GAME", "buttonCreateRoom.onClick invoked — popup should spawn.");
                buttonClicked = true;
                break;
            }
            if (buttonClicked) break;
        }

        if (!buttonClicked)
        {
            _lastActionStatus = "Could not find a live buttonCreateRoom to click.";
            _log.Warn("CREATE_GAME", _lastActionStatus);
            return;
        }

        // Phase 4: Defer popup configuration to the next poll cycle.
        // The popup needs at least one frame to initialize after buttonCreateRoom spawns it.
        // Using the existing retry mechanism (500ms intervals, up to 12 attempts).
        _log.Info("CREATE_GAME", "buttonCreateRoom clicked. Deferring popup configuration to allow initialization...");
        _lastActionStatus = "Waiting for game setup popup to initialize...";
        RunExecutorAction("create game", () => _executor.TryConfigureAndCreateGame(), source);
    }

    private void Poll(string source, bool force)
    {
        if (!_initialized)
            return;

        try
        {
            var now = DateTime.UtcNow;

            // Adaptive poll cadence. When we are stably in a room with no recovery,
            // navigation, or pending executor action in flight, nothing needs sub-second
            // polling — slow to 1s to cut the per-poll Capture() reflection cost during a
            // race. Drop back to 250ms whenever a state machine is active so it stays
            // responsive.
            var idleInRoom = _lastSnapshot != null
                && _lastSnapshot.IsInMultiplayer
                && _recoveryStep == 0
                && _navStep == 0
                && _pendingExecutorActions.Count == 0;
            //
            // A forced poll gets its own floor rather than no limit at all.
            // OnPlayerPropertiesUpdate / OnRoomPropertiesUpdate fire per pilot as
            // Photon streams race state, so an 8-pilot lobby delivered bursts of
            // forced polls -- each one a full Capture() with an object-graph scan
            // -- many times a second. 100ms collapses a burst into a single poll
            // while staying well inside the 250ms active cadence, and Update()
            // retries anything the floor swallowed.
            var minInterval = force
                ? ForcedPollFloor
                : idleInRoom
                    ? TimeSpan.FromMilliseconds(1000)
                    : TimeSpan.FromMilliseconds(250);
            if (now - _lastPollUtc < minInterval)
                return;

            _activityPending = false;
            _activitySource = null;
            _lastPollUtc = now;
            if (!_hasLoggedTickSource)
            {
                _hasLoggedTickSource = true;
                _log.Info("TICK", $"First multiplayer track control poll observed via {source}");
            }

            var snapshot = _hostDetector.Capture();
            _lastSnapshot = snapshot;
            if (snapshot.IsInMultiplayer != _wasInMultiplayer)
            {
                _wasInMultiplayer = snapshot.IsInMultiplayer;
                _log.Info("HOST", $"Multiplayer state changed: inMultiplayer={snapshot.IsInMultiplayer} reason={snapshot.InMultiplayerReason}");
                if (snapshot.IsInMultiplayer)
                {
                    _recoveryAttemptCount = 0; // Reset for next disconnect
                    _step4CreateGameRetries = 0;
                    _step4CreateGameFiredUtc = DateTime.MinValue;
                    _recoveryFirstFailureUtc = DateTime.MinValue; // Cleared on any successful rejoin
                    _roomLossDetectedUtc = DateTime.MinValue; // Rejoined — clear silent-loss timer
                }
                else
                {
                    // Note the moment we left the room so the silent-reconnect
                    // watchdog below can detect prolonged out-of-room state
                    // even when Photon didn't surface a Disconnected callback.
                    _roomLossDetectedUtc = DateTime.UtcNow;
                }
                if (snapshot.IsInMultiplayer && _config.AutoDumpOnLobbyJoin.Value)
                {
                    _discovery.DumpDiscovery();
                    _executor.DumpCurrentState();
                    if (_config.EnableChatDiagnostics.Value)
                        _chatService.DumpChatState();
                }
            }

            // Silent room-loss watchdog: if we lost the room but Photon is still
            // connected (i.e. back on the Master server) and recovery didn't kick
            // in via OnPhotonDisconnected, start it ourselves after a short grace.
            if (_config.EnableAutoRecovery.Value
                && _recoveryStep == 0
                && !snapshot.IsInMultiplayer
                && snapshot.IsPhotonConnected
                && _roomLossDetectedUtc != DateTime.MinValue
                && (now - _roomLossDetectedUtc) > SilentRoomLossGrace)
            {
                var awayFor = now - _roomLossDetectedUtc;
                _log.Warn("RECOVERY", $"Detected silent room loss — connected to Photon ({snapshot.PhotonClientState}) but not in a room for {awayFor.TotalSeconds:F1}s without a Disconnected callback.");
                _roomLossDetectedUtc = DateTime.MinValue; // Prevent re-entry while recovery runs
                StartRecovery("silent room loss (reconnected to Master without rejoin)");
            }

            if (snapshot.IsHost != _wasHost)
            {
                _wasHost = snapshot.IsHost;
                _log.Info("HOST", $"Host state changed: isHost={snapshot.IsHost} reason={snapshot.HostReason}");
            }

            // During recovery, IsInMultiplayer is intentionally false — that's the state
            // recovery is escaping. Cancelling the queued create-game here kills recovery's
            // own action mid-deferral and wedges Step 4 in a 12s × 4 retry loop. Recovery
            // owns the executor queue while running and clears it via CancelRecovery /
            // CompleteRecovery.
            if ((!snapshot.IsInMultiplayer || !snapshot.IsHost) && _pendingExecutorActions.Count > 0 && _recoveryStep == 0)
            {
                CancelPendingExecutorActions($"multiplayer/host state changed. inMultiplayer={snapshot.IsInMultiplayer} isHost={snapshot.IsHost}");
            }

            RetryPendingExecutorActions(now);

            // Drive multi-step navigation
            if (_navStep > 0 && now >= _navStepScheduledUtc)
            {
                RunNavStep("poll-continuation");
            }

            // Drive auto-recovery state machine
            if (_recoveryStep > 0 && now >= _recoveryScheduledUtc)
            {
                RunRecoveryStep("poll-continuation");
            }

            if (ShouldRunAction(_config.TriggerDiscoveryHotkey.Value, _config.RequestDiscoveryDump, "discovery dump"))
            {
                _discovery.DumpDiscovery();
            }

            if (ShouldRunAction(_config.TriggerStateDumpHotkey.Value, _config.RequestStateDump, "state dump"))
            {
                _executor.DumpCurrentState();
            }

            if (TryConsumeConfigCommand(_config.RequestDumpChatState, "chat state dump", allowDelayedClear: false))
            {
                _chatService.DumpChatState();
            }

            if (TryConsumeConfigCommand(_config.RequestSendChatMessage, "chat send", allowDelayedClear: false))
            {
                _chatService.SendConfiguredMessage();
            }

            if (TryConsumeConfigCommand(_config.RequestAdvanceSequence, "advance target sequence", allowDelayedClear: false))
            {
                QueueNextSequenceTarget();
            }

            if (TryConsumeConfigCommand(_config.RequestDumpLobbyState, "lobby state dump", allowDelayedClear: false))
            {
                _discovery.DumpLobbyState();
            }

            if (TryConsumeConfigCommand(_config.RequestDumpMainMenu, "main menu dump", allowDelayedClear: false))
            {
                _discovery.DumpMainMenuState();
            }

            if (TryConsumeConfigCommand(_config.RequestNavigateToLobby, "navigate to lobby", allowDelayedClear: false))
            {
                AttemptNavigateToMultiplayerLobby("config");
            }

            if (TryConsumeConfigCommand(_config.RequestCreateGame, "create game", allowDelayedClear: false))
            {
                AttemptCreateGame("config");
            }

            if (TryConsumeShortcut(_config.TriggerTrackChangeHotkey.Value, "configured track/race change"))
            {
                RunExecutorAction("configured track/race change", _executor.AttemptConfiguredChange, "hotkey");
            }
            else if (TryConsumeConfigCommand(_config.RequestTrackChange, "configured track/race change", allowDelayedClear: true))
            {
                RunExecutorAction("configured track/race change", _executor.AttemptConfiguredChange, "config", _config.RequestTrackChange);
            }

            if (TryConsumeShortcut(_config.TriggerCycleNextHotkey.Value, "cycle-next track/race"))
            {
                RunExecutorAction("cycle-next track/race", _executor.AttemptCycleNext, "hotkey");
            }
            else if (TryConsumeConfigCommand(_config.RequestCycleNext, "cycle-next track/race", allowDelayedClear: true))
            {
                RunExecutorAction("cycle-next track/race", _executor.AttemptCycleNext, "config", _config.RequestCycleNext);
            }
        }
        catch (Exception ex)
        {
            _log.Error("UPDATE", "Multiplayer track control update loop failed.", ex);
        }
    }

    public void OnPlayerEnteredRoom(Player newPlayer)
    {
        if (!_initialized)
            return;

        _log.Info("PHOTON", $"Player entered room: actor={newPlayer.ActorNumber} nick=\"{newPlayer.NickName}\"");
    }

    public void OnPlayerLeftRoom(Player otherPlayer)
    {
        if (!_initialized)
            return;

        _log.Info("PHOTON", $"Player left room: actor={otherPlayer.ActorNumber} nick=\"{otherPlayer.NickName}\"");
    }

    public void OnRoomPropertiesUpdate(Hashtable propertiesThatChanged)
    {
        if (!_initialized)
            return;

        _log.Info("PHOTON", () => $"Room properties changed: {ReflectionHelper.SafeDescribe(_describe, propertiesThatChanged)}");
    }

    public void OnPlayerPropertiesUpdate(Player player, Hashtable changedProps)
    {
        if (!_initialized)
            return;

        _log.Info("PHOTON", () => $"Player properties changed: actor={player.ActorNumber} nick=\"{player.NickName}\" props={ReflectionHelper.SafeDescribe(_describe, changedProps)}");
    }

    public void OnMasterClientSwitched(Player newMasterClient)
    {
        if (!_initialized)
            return;

        _log.Info("PHOTON", $"Master client switched: actor={newMasterClient.ActorNumber} nick=\"{newMasterClient.NickName}\"");
        _executor.DumpCurrentState();
    }

    public void Dispose()
    {
        _patches.Dispose();
    }

    /// <summary>Whether the debug panel is showing, so its IMGUI host can be off the rest of the time.</summary>
    public bool WantsGui => _initialized && _config.ShowDebugPanel.Value;

    public void OnGUI()
    {
        if (!WantsGui)
            return;

        _debugWindowRect = GUILayout.Window(
            GetHashCode(),
            _debugWindowRect,
            DrawDebugWindow,
            "MTC Debug");
    }

    private void DrawDebugWindow(int windowId)
    {
        var snapshot = _lastSnapshot ?? _hostDetector.Capture();

        // ── Status labels ──
        var photonColor = snapshot.IsPhotonConnected ? "lime" : "yellow";
        var photonLabel = snapshot.IsPhotonConnected ? "Connected" : snapshot.PhotonClientState;
        GUILayout.Label($"Photon: <color={photonColor}>{photonLabel}</color>", _richStyle ??= new GUIStyle(GUI.skin.label) { richText = true });

        var screenLabel = snapshot.IsInMultiplayer
            ? (snapshot.IsInLobbyWaitingRoom ? "In Room (Lobby)" : "In Room (In-Game)")
            : (snapshot.IsOnMultiplayerLobbyScreen ? "Multiplayer Lobby" : "Main Menu / Other");
        var screenColor = snapshot.IsOnMultiplayerLobbyScreen || snapshot.IsInMultiplayer ? "lime" : "yellow";
        GUILayout.Label($"Screen: <color={screenColor}>{screenLabel}</color>", _richStyle);

        GUILayout.Label($"In multiplayer: {snapshot.IsInMultiplayer} | Host: {snapshot.IsHost}");
        GUILayout.Label($"Dry run: {_config.EnableDryRun.Value}");

        if (!string.IsNullOrEmpty(_config.TargetEnvironmentName.Value) ||
            !string.IsNullOrEmpty(_config.TargetTrackName.Value) ||
            !string.IsNullOrEmpty(_config.TargetRaceName.Value))
        {
            GUILayout.Label($"Target: {_config.TargetEnvironmentName.Value} / {_config.TargetTrackName.Value} / {_config.TargetRaceName.Value}");
        }

        GUILayout.Space(4);

        // ── Navigation + Create Game ──
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Navigate to MP Lobby"))
            RunUiAction("Navigate to lobby requested from debug panel.", () => AttemptNavigateToMultiplayerLobby("debug-panel"));
        if (GUILayout.Button("Create Game"))
            RunUiAction("Create game requested from debug panel.", () => AttemptCreateGame("debug-panel"));
        GUILayout.EndHorizontal();

        GUILayout.Space(2);

        // ── Diagnostics ──
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Dump Discovery"))
            RunUiAction("Discovery dump requested from debug panel.", () => _discovery.DumpDiscovery());
        if (GUILayout.Button("Dump State"))
            RunUiAction("State dump requested from debug panel.", _executor.DumpCurrentState);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Dump Lobby State"))
            RunUiAction("Lobby state dump requested from debug panel.", _discovery.DumpLobbyState);
        if (GUILayout.Button("Dump Main Menu"))
            RunUiAction("Main menu dump requested from debug panel.", _discovery.DumpMainMenuState);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Dump Chat"))
            RunUiAction("Chat state dump requested from debug panel.", _chatService.DumpChatState);
        if (GUILayout.Button("Dump Buttons"))
            RunUiAction("Button dump requested from debug panel.", _discovery.DumpAllActiveButtons);
        GUILayout.EndHorizontal();

        GUILayout.Space(2);

        // ── Track Control ──
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Attempt Configured Change"))
            RunUiExecutorAction("Configured track/race change requested from debug panel.", "configured track/race change", _executor.AttemptConfiguredChange);
        if (GUILayout.Button("Cycle Next"))
            RunUiExecutorAction("Cycle-next requested from debug panel.", "cycle-next track/race", _executor.AttemptCycleNext);
        GUILayout.EndHorizontal();

        if (GUILayout.Button("Advance Sequence"))
            RunUiAction("Advance target sequence requested from debug panel.", QueueNextSequenceTarget);

        // ── Recovery status ──
        if (_recoveryStep > 0)
        {
            GUILayout.Space(4);
            GUILayout.Label($"<color=orange>Recovery step {_recoveryStep}/5 (attempt {_recoveryAttemptCount}/{MaxRecoveryAttempts})</color>", _richStyle);
        }

        // ── Status ──
        if (!string.IsNullOrEmpty(_lastActionStatus))
        {
            GUILayout.Space(4);
            GUILayout.Label($"<color=cyan>{_lastActionStatus}</color>", _richStyle);
        }

        GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
    }

    private GUIStyle? _richStyle;

    private void RunUiAction(string message, Action action)
    {
        try
        {
            _log.Info("UI", message);
            action();
        }
        catch (Exception ex)
        {
            _log.Error("UI", $"Debug panel action failed: {message}", ex);
        }
    }

    private void RunUiExecutorAction(string message, string actionName, Func<MultiplayerTrackChangeExecutionStatus> action)
    {
        try
        {
            _log.Info("UI", message);
            RunExecutorAction(actionName, action, "debug-panel");
        }
        catch (Exception ex)
        {
            _log.Error("UI", $"Debug panel executor action failed: {message}", ex);
        }
    }

    private bool ShouldRunAction(KeyboardShortcut hotkey, ConfigEntry<bool> commandEntry, string actionName)
    {
        if (TryConsumeShortcut(hotkey, actionName))
            return true;

        if (TryConsumeConfigCommand(commandEntry, actionName, allowDelayedClear: false))
            return true;

        return false;
    }

    private bool TryConsumeShortcut(KeyboardShortcut shortcut, string actionName)
    {
        if (shortcut.MainKey == KeyCode.None)
            return false;

        if (shortcut.IsDown())
        {
            _log.Info("HOTKEY", $"{actionName} requested via KeyboardShortcut {FormatShortcut(shortcut)}");
            return true;
        }

        if (!_config.EnableRawHotkeyFallback.Value)
            return false;

        if (!TryConsumeRawShortcut(shortcut, out var detail))
            return false;

        _log.Info("HOTKEY", $"{actionName} requested via raw input {detail}");
        return true;
    }

    private bool TryConsumeRawShortcut(KeyboardShortcut shortcut, out string detail)
    {
        detail = string.Empty;
        if (!Input.GetKeyDown(shortcut.MainKey))
            return false;

        var modifiers = shortcut.Modifiers.ToArray();
        if (modifiers.Any(modifier => !Input.GetKey(modifier)))
            return false;

        detail = $"{shortcut.MainKey} modifiers=[{string.Join(", ", modifiers.Select(modifier => modifier.ToString()))}]";
        return true;
    }

    private bool TryConsumeConfigCommand(ConfigEntry<bool> commandEntry, string actionName, bool allowDelayedClear)
    {
        if (!commandEntry.Value)
            return false;

        var commandKey = GetCommandKey(commandEntry);
        if (allowDelayedClear && _activeConfigCommandKeys.Contains(commandKey))
            return false;

        _log.Info("COMMAND", $"{actionName} requested via config entry {commandEntry.Definition.Section}.{commandEntry.Definition.Key}");
        if (allowDelayedClear && _config.DelayExecutorCommandClearUntilCompletion.Value)
        {
            _activeConfigCommandKeys.Add(commandKey);
        }
        else
        {
            ClearConfigCommandEntry(commandEntry, $"consumed for {actionName}");
        }

        return true;
    }

    private void RunExecutorAction(
        string actionName,
        Func<MultiplayerTrackChangeExecutionStatus> action,
        string sourceDescription,
        ConfigEntry<bool>? commandEntry = null)
    {
        var existingPending = FindPendingAction(actionName);
        if (existingPending != null)
        {
            existingPending.NextAttemptUtc = DateTime.UtcNow;
            _log.Info("EXEC", $"Action \"{actionName}\" is already queued. source={sourceDescription} nextAttempt={existingPending.NextAttemptUtc:O}");
            return;
        }

        var status = action();
        HandleExecutorStatus(new PendingExecutorAction(actionName, action, commandEntry, sourceDescription) { AttemptsStarted = 1 }, status, isRetry: false);
    }

    private void RetryPendingExecutorActions(DateTime now)
    {
        var dueActions = _pendingExecutorActions
            .Where(pending => now >= pending.NextAttemptUtc)
            .OrderBy(pending => pending.NextAttemptUtc)
            .ToList();

        foreach (var pendingAction in dueActions)
        {
            if (pendingAction.AttemptsStarted >= MaxPendingRetryAttempts)
            {
                _log.Warn("EXEC", $"Giving up on pending action \"{pendingAction.Name}\" after {pendingAction.AttemptsStarted} attempts without a live popup.");
                FinalizeExecutorAction(pendingAction, MultiplayerTrackChangeExecutionStatus.Failed, "Timed out waiting for a live popup/controller.");
                continue;
            }

            pendingAction.AttemptsStarted++;
            if (pendingAction.AttemptsStarted <= 1 || pendingAction.AttemptsStarted == 6 || pendingAction.AttemptsStarted >= MaxPendingRetryAttempts)
                _log.Info("EXEC", $"Retrying deferred action \"{pendingAction.Name}\" attempt {pendingAction.AttemptsStarted}/{MaxPendingRetryAttempts}.");
            var status = pendingAction.Action();
            HandleExecutorStatus(pendingAction, status, isRetry: true);
        }
    }

    private void HandleExecutorStatus(PendingExecutorAction pendingAction, MultiplayerTrackChangeExecutionStatus status, bool isRetry)
    {
        if (status == MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup)
        {
            SchedulePendingExecutorAction(pendingAction, isRetry);
            return;
        }

        FinalizeExecutorAction(pendingAction, status, detail: null);
    }

    private void SchedulePendingExecutorAction(PendingExecutorAction pendingAction, bool isRetry)
    {
        var now = DateTime.UtcNow;
        var existingPending = FindPendingAction(pendingAction.Name);
        if (existingPending == null)
        {
            pendingAction.AttemptsStarted = Math.Max(pendingAction.AttemptsStarted, 1);
            pendingAction.NextAttemptUtc = now + PendingRetryDelay;
            _pendingExecutorActions.Add(pendingAction);
            LogExecutorSummary("EXEC", pendingAction, MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup, $"Queued until a live popup/controller becomes available. nextAttempt={pendingAction.NextAttemptUtc:O}");
            return;
        }

        if (!isRetry)
        {
            LogExecutorSummary("EXEC", existingPending, MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup, $"Already queued. nextAttempt={existingPending.NextAttemptUtc:O}");
            return;
        }

        existingPending.NextAttemptUtc = now + PendingRetryDelay;
        LogExecutorSummary("EXEC", existingPending, MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup, $"Still waiting for a live popup/controller. nextAttempt={existingPending.NextAttemptUtc:O}");
    }

    private void FinalizeExecutorAction(PendingExecutorAction pendingAction, MultiplayerTrackChangeExecutionStatus status, string? detail)
    {
        RemovePendingAction(pendingAction);
        CompleteExecutorConfigCommandIfNeeded(pendingAction, status);

        if (string.Equals(pendingAction.Name, "create game", StringComparison.OrdinalIgnoreCase))
        {
            _lastCreateGameStatus = status;
            _lastCreateGameStatusUtc = DateTime.UtcNow;
        }

        var category = status switch
        {
            MultiplayerTrackChangeExecutionStatus.Applied => "EXEC",
            MultiplayerTrackChangeExecutionStatus.DryRun => "EXEC",
            _ => "EXEC"
        };

        LogExecutorSummary(category, pendingAction, status, detail);

        if (status == MultiplayerTrackChangeExecutionStatus.Applied &&
            pendingAction.Name == "configured track/race change" &&
            _config.AnnounceTrackChangesInChat.Value)
        {
            _chatService.SendAnnouncement(
                _config.TargetEnvironmentName.Value,
                _config.TargetTrackName.Value,
                _config.TargetRaceName.Value,
                _config.TargetWorkshopId.Value);
        }
    }

    private void CancelPendingExecutorActions(string reason)
    {
        var pendingActions = _pendingExecutorActions.ToList();
        foreach (var pendingAction in pendingActions)
        {
            // Route through Finalize so _lastCreateGameStatus is updated for "create game".
            // Without this, recovery Step 4 sees hasFreshOutcome=false after a cancellation
            // and waits 12s before retrying.
            FinalizeExecutorAction(pendingAction, MultiplayerTrackChangeExecutionStatus.Failed, $"Cancelled because {reason}");
        }
    }

    private void CompleteExecutorConfigCommandIfNeeded(PendingExecutorAction pendingAction, MultiplayerTrackChangeExecutionStatus status)
    {
        if (pendingAction.CommandEntry == null)
            return;

        var commandKey = pendingAction.CommandKey;
        if (!string.IsNullOrWhiteSpace(commandKey))
            _activeConfigCommandKeys.Remove(commandKey);

        if (_config.DelayExecutorCommandClearUntilCompletion.Value && pendingAction.CommandEntry.Value)
            ClearConfigCommandEntry(pendingAction.CommandEntry, $"terminal executor status={status}");
    }

    private void ClearConfigCommandEntry(ConfigEntry<bool> commandEntry, string reason)
    {
        if (!commandEntry.Value)
            return;

        commandEntry.Value = false;
        _plugin.Config.Save();
        _log.Info("COMMAND", $"Cleared config entry {commandEntry.Definition.Section}.{commandEntry.Definition.Key} ({reason}).");
    }

    private static string GetCommandKey(ConfigEntry<bool> commandEntry)
    {
        return $"{commandEntry.Definition.Section}.{commandEntry.Definition.Key}";
    }

    private PendingExecutorAction? FindPendingAction(string actionName)
    {
        return _pendingExecutorActions.FirstOrDefault(pending => string.Equals(pending.Name, actionName, StringComparison.OrdinalIgnoreCase));
    }

    private void RemovePendingAction(PendingExecutorAction pendingAction)
    {
        _pendingExecutorActions.RemoveAll(existing => string.Equals(existing.Name, pendingAction.Name, StringComparison.OrdinalIgnoreCase));
    }

    private void LogExecutorSummary(string category, PendingExecutorAction pendingAction, MultiplayerTrackChangeExecutionStatus status, string? detail)
    {
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" detail={detail}";
        var message = $"Action \"{pendingAction.Name}\" status={status} attempts={pendingAction.AttemptsStarted}/{MaxPendingRetryAttempts} source={pendingAction.SourceDescription}{suffix}";

        switch (status)
        {
            case MultiplayerTrackChangeExecutionStatus.Applied:
            case MultiplayerTrackChangeExecutionStatus.DryRun:
                _log.Info(category, message);
                break;
            case MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup:
                _log.Info(category, message);
                break;
            default:
                _log.Warn(category, message);
                break;
        }
    }

    private void QueueNextSequenceTarget()
    {
        var entries = MultiplayerTargetSequence.Parse(_config.TargetSequence.Value, _log);
        if (entries.Count == 0)
        {
            _log.Warn("SEQUENCE", "TargetSequence is empty or contained no valid entries.");
            return;
        }

        if (!TryReadCurrentRoomTargets(out var currentEnvironment, out var currentTrack, out var currentRace))
        {
            _log.Warn("SEQUENCE", "Could not read current room environment/track/race. Falling back to the first sequence entry.");
            ApplySequenceEntry(entries[0]);
            RunExecutorAction("configured track/race change", _executor.AttemptConfiguredChange, "sequence");
            return;
        }

        _log.Info("SEQUENCE", $"Current room target before advance: env=\"{currentEnvironment}\" track=\"{currentTrack}\" race=\"{currentRace}\"");

        if (!MultiplayerTargetSequence.TrySelectNext(entries, currentEnvironment, currentTrack, currentRace, _config.LoopTargetSequence.Value, out var next))
        {
            _log.Warn("SEQUENCE", $"No next sequence entry was available. current={currentEnvironment}|{currentTrack}|{currentRace} loop={_config.LoopTargetSequence.Value}");
            return;
        }

        ApplySequenceEntry(next);
        RunExecutorAction("configured track/race change", _executor.AttemptConfiguredChange, "sequence");
    }

    private bool TryReadCurrentRoomTargets(out string environmentName, out string trackName, out string raceName)
    {
        environmentName = string.Empty;
        trackName = string.Empty;
        raceName = string.Empty;

        var room = Photon.Pun.PhotonNetwork.CurrentRoom;
        if (room?.CustomProperties == null)
            return false;

        if (room.CustomProperties.TryGetValue("E", out var environment))
            environmentName = environment as string ?? string.Empty;

        if (room.CustomProperties.TryGetValue("T", out var track))
            trackName = ReflectionHelper.GetMemberValue(track, "Name") as string ?? track?.ToString() ?? string.Empty;

        if (room.CustomProperties.TryGetValue("R", out var race))
            raceName = ReflectionHelper.GetMemberValue(race, "Name") as string ?? race?.ToString() ?? string.Empty;

        return !string.IsNullOrWhiteSpace(environmentName) ||
               !string.IsNullOrWhiteSpace(trackName) ||
               !string.IsNullOrWhiteSpace(raceName);
    }

    private void ApplySequenceEntry(MultiplayerTargetSequence.SequenceEntry entry)
    {
        _config.TargetEnvironmentName.Value = entry.EnvironmentName;
        _config.TargetTrackName.Value = entry.TrackName;
        _config.TargetRaceName.Value = entry.RaceName;
        _config.TargetWorkshopId.Value = entry.WorkshopId;
        _plugin.Config.Save();

        _log.Info("SEQUENCE", $"Applied next sequence target: env=\"{entry.EnvironmentName}\" track=\"{entry.TrackName}\" race=\"{entry.RaceName}\" workshop=\"{entry.WorkshopId}\"");
    }

    private static string FormatShortcut(KeyboardShortcut shortcut)
    {
        var modifiers = shortcut.Modifiers.ToArray();
        if (modifiers.Length == 0)
            return shortcut.MainKey.ToString();

        return string.Join(" + ", modifiers.Select(modifier => modifier.ToString()).Concat(new[] { shortcut.MainKey.ToString() }));
    }

    private static int GetObjectId(object value)
    {
        return value is UnityEngine.Object unityObject ? unityObject.GetInstanceID() : value.GetHashCode();
    }

    private sealed class PendingExecutorAction
    {
        public PendingExecutorAction(
            string name,
            Func<MultiplayerTrackChangeExecutionStatus> action,
            ConfigEntry<bool>? commandEntry,
            string sourceDescription)
        {
            Name = name;
            Action = action;
            CommandEntry = commandEntry;
            SourceDescription = sourceDescription;
            CommandKey = commandEntry == null ? string.Empty : GetCommandKey(commandEntry);
        }

        public string Name { get; }
        public Func<MultiplayerTrackChangeExecutionStatus> Action { get; }
        public ConfigEntry<bool>? CommandEntry { get; }
        public string SourceDescription { get; }
        public string CommandKey { get; }
        public int AttemptsStarted { get; set; }
        public DateTime NextAttemptUtc { get; set; }
    }
}
