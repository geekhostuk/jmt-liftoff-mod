using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using JmtLiftoffMod.Features.Competition;
using Photon.Pun;
using ExitGames.Client.Photon;
using UnityEngine;
using UnityEngine.UI;

namespace JmtLiftoffMod.Features.MultiplayerTrackControl;

internal enum MultiplayerTrackChangeExecutionStatus
{
    Applied,
    DryRun,
    DeferredWaitingForActivePopup,
    NoMatchingCandidate,
    AbortedNotInMultiplayer,
    AbortedNotHost,
    Failed
}

internal enum PopupAcquireStatus
{
    Acquired,
    WaitingForActivePopup,
    Unavailable
}

internal sealed class MultiplayerTrackChangeExecutor : ITrackControlAdapter
{
    private readonly MultiplayerTrackControlConfig _config;
    private readonly MultiplayerDiscoveryService _discovery;
    private readonly MultiplayerHostStateDetector _hostDetector;
    private readonly MultiplayerTrackControlLog _log;
    private readonly Func<object?, string> _describe;

    // Tracks popup instance IDs detected as zombie so they are not re-selected
    // on subsequent create-game retries. Cleared on recovery completion.
    private readonly HashSet<int> _zombiePopupIds = new();

    // ── Per-execution diagnostics (ambient via ThreadStatic) ─────────────
    [ThreadStatic] private static ExecutionDiagnostics? _diag;

    private sealed class ExecutionDiagnostics
    {
        public int FindObjectsOfTypeAllCalls;
        public int GetDropdownDataOptionsCalls;
        public int GetDropdownDataOptionsTotalItems;
        public int MakeGenericMethodCalls;
        public int GetDropdownCandidatesCalls;
        public int GetDropdownCandidatesTotalItems;
        public int GetSelectionOptionSetsCalls;
        public int TrySearchAcrossEnvironmentsCalls;
        public int EnvironmentCandidatesIterated;

        public string ToSummary()
        {
            var avgOpts = GetDropdownDataOptionsCalls > 0
                ? GetDropdownDataOptionsTotalItems / GetDropdownDataOptionsCalls
                : 0;
            return $"FindObjectsOfTypeAll={FindObjectsOfTypeAllCalls} " +
                   $"GetDropdownDataOptions={GetDropdownDataOptionsCalls}(avg={avgOpts}opts) " +
                   $"MakeGenericMethod={MakeGenericMethodCalls} " +
                   $"GetDropdownCandidates={GetDropdownCandidatesCalls}(total={GetDropdownCandidatesTotalItems}) " +
                   $"GetSelectionOptionSets={GetSelectionOptionSetsCalls} " +
                   $"TrySearchAcrossEnv={TrySearchAcrossEnvironmentsCalls}(envs={EnvironmentCandidatesIterated})";
        }
    }

    public MultiplayerTrackChangeExecutor(
        MultiplayerTrackControlConfig config,
        MultiplayerDiscoveryService discovery,
        MultiplayerHostStateDetector hostDetector,
        MultiplayerTrackControlLog log,
        Func<object?, string> describe)
    {
        _config = config;
        _discovery = discovery;
        _hostDetector = hostDetector;
        _log = log;
        _describe = describe;
    }

    public void ClearZombieBlacklist() => _zombiePopupIds.Clear();

    public void DumpCurrentState()
    {
        _hostDetector.LogSnapshot("STATE");
        _log.Info("STATE", $"IsInLobbyWaitingRoom={_hostDetector.IsInLobbyWaitingRoom()}");
        DumpKnownLiveObjects("STATE", "Liftoff.Multiplayer.GameSetup.PopupQuickPlayMultiplayerSetup");
        DumpKnownLiveObjects("STATE", "MultiplayerRaceScoreButtonPanel");
        DumpKnownLiveObjects("STATE", "InGameMenuMainPanel");

        // Dump lobby controller candidates
        var lobbyTypes = _discovery.FindTypesByFieldName("prefabPopupMultiplayerSetup");
        foreach (var type in lobbyTypes)
        {
            var liveObjects = ReflectionHelper.GetLiveObjects(type);
            _log.Info("STATE", $"Lobby controller type={type.FullName} liveCount={liveObjects.Count}");
        }
    }

    public MultiplayerTrackChangeExecutionStatus AttemptConfiguredChange()
    {
        return Execute(BuildConfiguredRequest());
    }

    public MultiplayerTrackChangeExecutionStatus AttemptConfiguredChange(CommandTimingContext? timing)
    {
        return Execute(BuildConfiguredRequest(), timing);
    }

    public MultiplayerTrackChangeExecutionStatus AttemptCycleNext()
    {
        return Execute(new ChangeRequest { CycleNext = true });
    }

    // NOTE: WarmUpPopup removed — warmup result was discarded by set_track, adding ~17-20s of load per bot with no benefit.

    /// <summary>
    /// Attempts to configure and create a game via the PopupQuickPlayMultiplayerSetup.
    /// Unlike the track-change flow, this skips host/multiplayer guards since we're creating
    /// a new game rather than changing settings in an existing one.
    /// Can accept a popup instance spawned by clicking buttonCreateRoom, or will try to
    /// find/acquire one itself.
    /// </summary>
    public MultiplayerTrackChangeExecutionStatus TryConfigureAndCreateGame(object? spawnedPopup = null)
    {
        _diag = new ExecutionDiagnostics();
        ReflectionHelper.OnFindObjectsOfTypeAllCalled = () => { if (_diag != null) _diag.FindObjectsOfTypeAllCalls++; };
        try
        {
            var result = TryConfigureAndCreateGameInner(spawnedPopup);
            // Only dump diagnostics when we did meaningful work (not on deferred/early exits)
            if (result != MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup)
                LogDiagnosticsSummary(null);
            return result;
        }
        finally
        {
            ReflectionHelper.OnFindObjectsOfTypeAllCalled = null;
            _diag = null;
        }
    }

    private MultiplayerTrackChangeExecutionStatus TryConfigureAndCreateGameInner(object? spawnedPopup)
    {
        _discovery.Refresh();

        _log.Info("CREATE", $"TryConfigureAndCreateGame started. spawnedPopup={spawnedPopup != null} dryRun={_config.EnableDryRun.Value}");

        PopupContext popupContext;

        // Try the supplied popup first
        if (spawnedPopup != null && TryBuildPopupContext(spawnedPopup, out popupContext))
        {
            _log.Info("CREATE", $"Using supplied popup: {ReflectionHelper.DescribeObjectIdentity(spawnedPopup)} active={IsPopupActive(spawnedPopup)}");
        }
        else
        {
            // Try to find any live popup
            var popupType = _discovery.TryResolveKnownType("Liftoff.Multiplayer.GameSetup.PopupQuickPlayMultiplayerSetup");
            if (popupType == null)
            {
                _log.Warn("CREATE", "PopupQuickPlayMultiplayerSetup type not resolved.");
                return MultiplayerTrackChangeExecutionStatus.Failed;
            }

            var livePopups = ReflectionHelper.GetLiveObjects(popupType)
                .Where(p => !_zombiePopupIds.Contains(GetInstanceId(p)))
                .ToList();
            var activePopup = livePopups.FirstOrDefault(IsPopupActive) ?? livePopups.FirstOrDefault();
            if (activePopup == null)
            {
                _log.Warn("CREATE", "No live PopupQuickPlayMultiplayerSetup instances found. The game creation popup may not have spawned yet.");
                return MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup;
            }

            if (!TryBuildPopupContext(activePopup, out popupContext))
            {
                _log.Warn("CREATE", $"Could not build popup context from: {ReflectionHelper.DescribeObjectIdentity(activePopup)}");
                return MultiplayerTrackChangeExecutionStatus.Failed;
            }

            _log.Info("CREATE", $"Using found popup: {ReflectionHelper.DescribeObjectIdentity(activePopup)} active={IsPopupActive(activePopup)}");
        }

        if (!IsPopupHealthy(popupContext, out var popupUnhealthyReason))
        {
            _log.Warn("CREATE", $"Popup ContentSettingsPanel is zombie ({popupUnhealthyReason}). Blacklisting instance and deferring to let recovery re-open a fresh popup.");
            _zombiePopupIds.Add(GetInstanceId(popupContext.Popup));
            MultiplayerRuntimeState.ClearLatestPopupObject();
            return MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup;
        }

        // Prepare the popup (populate content if empty)
        try
        {
            EnsurePopupPrepared(popupContext);
        }
        catch (Exception ex)
        {
            _log.Warn("CREATE", $"EnsurePopupPrepared failed (non-fatal): {ex.GetType().Name}: {ex.Message}");
        }

        LogOptionSnapshot(popupContext);

        // Configure with target settings if any are specified
        var request = BuildConfiguredRequest();
        var hasTargets = !string.IsNullOrWhiteSpace(request.EnvironmentName) ||
                         !string.IsNullOrWhiteSpace(request.TrackName) ||
                         !string.IsNullOrWhiteSpace(request.RaceName) ||
                         !string.IsNullOrWhiteSpace(request.WorkshopId);

        if (hasTargets)
        {
            if (ConfigurePopupForRequest(popupContext, request, out var selectionSummary))
            {
                _log.Info("CREATE", $"Popup configured: {selectionSummary}");
            }
            else
            {
                _log.Warn("CREATE", "Could not configure popup with targets — will use default/last settings.");
            }
        }
        else
        {
            _log.Info("CREATE", "No target env/track/race configured — using default/last session settings.");
        }

        if (_config.EnableDryRun.Value)
        {
            _log.Info("CREATE", "Dry-run enabled. Skipping game creation. Popup is configured with current settings.");
            DumpCurrentState();
            return MultiplayerTrackChangeExecutionStatus.DryRun;
        }

        PreInitBatterySpecificationToggles(popupContext);

        // Final pre-invoke health probe — popup state can transition between acquisition
        // and invocation (e.g. scene swap mid-recovery). Detect zombie now to avoid the
        // NullReferenceException inside OnSetGame that previously wedged Step 4 for 30s.
        if (!IsPopupHealthy(popupContext, out var preInvokeReason))
        {
            _log.Warn("CREATE", $"Skipping OnSetGame because popup went zombie before invocation: {preInvokeReason}");
            _zombiePopupIds.Add(GetInstanceId(popupContext.Popup));
            MultiplayerRuntimeState.ClearLatestPopupObject();
            return MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup;
        }

        // Apply: call OnSetGame directly. The popup's onSetGame event is hooked to the
        // lobby controller's handler which creates the Photon room.
        // NOTE: buttonCreateGame.onClick.Invoke() does not work because the popup's
        // button listeners are wired internally and may not be set up for programmatic clicks.
        var onSetGame = ReflectionHelper.FindMethod(popupContext.Popup.GetType(), "OnSetGame", 0);
        if (onSetGame != null)
        {
            _log.Info("CREATE", $"Invoking {ReflectionHelper.FormatMethodSignature(onSetGame)} to create game.");
            try
            {
                onSetGame.Invoke(popupContext.Popup, Array.Empty<object>());
                _log.Info("CREATE", "OnSetGame invoked — game creation triggered.");
                return MultiplayerTrackChangeExecutionStatus.Applied;
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                _log.Error("CREATE", $"OnSetGame threw {inner.GetType().Name}: {inner.Message}. Discarding popup so the next attempt rediscovers a fresh one.");
                MultiplayerRuntimeState.ClearLatestPopupObject();
                // Return Deferred (not Failed) so the action queue retries with a fresh popup.
                return MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup;
            }
        }

        // Fallback: try the onSetGame event delegate directly
        if (TryInvokePopupSetGameCallback(popupContext))
        {
            _log.Info("CREATE", "Game creation triggered via cached onSetGame callback.");
            return MultiplayerTrackChangeExecutionStatus.Applied;
        }

        _log.Warn("CREATE", "No OnSetGame method or callback found on popup.");
        return MultiplayerTrackChangeExecutionStatus.Failed;
    }

    private ChangeRequest BuildConfiguredRequest()
    {
        return new ChangeRequest
        {
            EnvironmentName = _config.TargetEnvironmentName.Value?.Trim() ?? string.Empty,
            TrackName = _config.TargetTrackName.Value?.Trim() ?? string.Empty,
            RaceName = _config.TargetRaceName.Value?.Trim() ?? string.Empty,
            WorkshopId = _config.TargetWorkshopId.Value?.Trim() ?? string.Empty,
            CycleNext = false
        };
    }

    private MultiplayerTrackChangeExecutionStatus Execute(ChangeRequest request, CommandTimingContext? timing = null)
    {
        _diag = new ExecutionDiagnostics();
        ReflectionHelper.OnFindObjectsOfTypeAllCalled = () => { if (_diag != null) _diag.FindObjectsOfTypeAllCalls++; };
        try
        {
            return ExecuteInner(request, timing);
        }
        finally
        {
            LogDiagnosticsSummary(timing);
            ReflectionHelper.OnFindObjectsOfTypeAllCalled = null;
            _diag = null;
        }
    }

    private void LogDiagnosticsSummary(CommandTimingContext? timing)
    {
        if (_diag == null) return;
        var summary = _diag.ToSummary();
        _log.Info("DIAG", summary);
        timing?.SetDiagnostics(summary);
    }

    private MultiplayerTrackChangeExecutionStatus ExecuteInner(ChangeRequest request, CommandTimingContext? timing)
    {
        timing?.StartPhase("discovery_refresh");
        _discovery.Refresh();
        timing?.StartPhase("host_capture");
        var host = _hostDetector.Capture();

        _log.Info("EXEC", $"Requested change: environment=\"{request.EnvironmentName}\" track=\"{request.TrackName}\" race=\"{request.RaceName}\" workshop=\"{request.WorkshopId}\" cycle={request.CycleNext} dryRun={_config.EnableDryRun.Value}");

        if (!host.IsInMultiplayer)
        {
            _log.Warn("EXEC", $"Aborted because multiplayer is not active. reason={host.InMultiplayerReason}");
            timing?.EndCurrentPhase();
            return MultiplayerTrackChangeExecutionStatus.AbortedNotInMultiplayer;
        }

        if (!host.IsHost)
        {
            _log.Warn("EXEC", $"Aborted because local player is not host. reason={host.HostReason}");
            timing?.EndCurrentPhase();
            return MultiplayerTrackChangeExecutionStatus.AbortedNotHost;
        }

        timing?.StartPhase("popup_acquire");
        if (!TryAcquirePopup(out var popupContext, out var popupAcquireStatus))
        {
            timing?.EndCurrentPhase();
            if (popupAcquireStatus == PopupAcquireStatus.WaitingForActivePopup)
            {
                _log.Warn("EXEC", "Deferring track/race change because no live popup/controller instance is active yet.");
                return MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup;
            }

            _log.Warn("EXEC", "No multiplayer settings popup/controller path was available. TODO: validate additional runtime entry points.");
            return MultiplayerTrackChangeExecutionStatus.Failed;
        }

        try
        {
            timing?.StartPhase("popup_prepare");
            EnsurePopupPrepared(popupContext);

            LogOptionSnapshot(popupContext);

            timing?.StartPhase("popup_configure");
            if (!ConfigurePopupForRequest(popupContext, request, out var selectionSummary))
            {
                _log.Warn("EXEC", "Popup was acquired but no matching track/race candidate was found.");
                DumpPopupOptions(popupContext);
                timing?.EndCurrentPhase();
                return MultiplayerTrackChangeExecutionStatus.NoMatchingCandidate;
            }

            _log.Info("EXEC", $"Selection prepared: {selectionSummary}");

            if (_config.EnableDryRun.Value)
            {
                _log.Info("EXEC", "Dry-run enabled. Skipping PopupQuickPlayMultiplayerSetup.OnSetGame invocation.");
                DumpCurrentState();
                timing?.EndCurrentPhase();
                return MultiplayerTrackChangeExecutionStatus.DryRun;
            }

            // Lobby path: use the game's own "Change Settings" flow.
            // Find the waiting room's change-settings button, click it to open a properly-hooked
            // popup, configure that popup with our track, then auto-click "Set Game".
            timing?.StartPhase("apply");
            if (_lastLobbyController != null && _lastLobbyControllerType != null)
            {
                var applied = TryApplyViaChangeSettingsButton(request);
                if (applied)
                    return MultiplayerTrackChangeExecutionStatus.Applied;
                _log.Warn("EXEC", "Lobby Change Settings path failed, falling through to standard paths.");
            }

            if (!IsPopupActive(popupContext.Popup) &&
                _config.EnableCachedSetGameCallbackFallback.Value &&
                TryInvokePopupSetGameCallback(popupContext))
            {
                _log.Info("EXEC", "Track/race change invocation finished via popup onSetGame callback.");
                return MultiplayerTrackChangeExecutionStatus.Applied;
            }

            if (!IsPopupActive(popupContext.Popup))
            {
                _log.Warn("EXEC", "Aborting final apply because popup is inactive and cached callback fallback is disabled.");
                timing?.EndCurrentPhase();
                return MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup;
            }

            if (!IsPopupHealthy(popupContext, out var preInvokeReason))
            {
                _log.Warn("EXEC", $"Skipping OnSetGame because popup went zombie before invocation: {preInvokeReason}");
                MultiplayerRuntimeState.ClearLatestPopupObject();
                timing?.EndCurrentPhase();
                return MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup;
            }

            PreInitBatterySpecificationToggles(popupContext);

            var onSetGame = ReflectionHelper.FindMethod(popupContext.Popup.GetType(), "OnSetGame", 0);
            if (onSetGame == null)
            {
                _log.Warn("EXEC", "PopupQuickPlayMultiplayerSetup.OnSetGame could not be found.");
                timing?.EndCurrentPhase();
                return MultiplayerTrackChangeExecutionStatus.Failed;
            }

            _log.Info("EXEC", $"Invoking {ReflectionHelper.FormatMethodSignature(onSetGame)}");
            try
            {
                onSetGame.Invoke(popupContext.Popup, Array.Empty<object>());
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                _log.Error("EXEC", $"OnSetGame threw {inner.GetType().Name}: {inner.Message}. Discarding popup so the next attempt rediscovers a fresh one.");
                MultiplayerRuntimeState.ClearLatestPopupObject();
                timing?.EndCurrentPhase();
                return MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup;
            }
            _log.Info("EXEC", "Track/race change invocation finished.");
            return MultiplayerTrackChangeExecutionStatus.Applied;
        }
        catch (Exception ex)
        {
            _log.Error("EXEC", "Track/race change execution failed.", ex);
            timing?.EndCurrentPhase();
            return MultiplayerTrackChangeExecutionStatus.Failed;
        }
    }

    private bool TryAcquirePopup(out PopupContext popupContext, out PopupAcquireStatus popupAcquireStatus)
    {
        popupContext = default;
        popupAcquireStatus = PopupAcquireStatus.Unavailable;

        var popupType = _discovery.TryResolveKnownType("Liftoff.Multiplayer.GameSetup.PopupQuickPlayMultiplayerSetup");
        if (popupType == null)
            return false;

        if (MultiplayerRuntimeState.TryGetLatestPopupWithSetGameCallback(out var callbackPopup, out _, out var callbackSource, out var callbackCapturedUtc) &&
            callbackPopup != null)
        {
            if (callbackPopup is UnityEngine.Object unityPopup && unityPopup == null)
            {
                _log.Warn("EXEC", $"Discarding cached callback popup from {callbackSource} because the Unity object is destroyed.");
                MultiplayerRuntimeState.ClearLatestPopupObject();
            }
            else if (GetInstanceId(callbackPopup) == 0)
            {
                _log.Warn("EXEC", $"Discarding cached callback popup from {callbackSource} because it has an invalid instance id: {ReflectionHelper.DescribeObjectIdentity(callbackPopup)}");
                MultiplayerRuntimeState.ClearLatestPopupObject();
            }
            else if (TryBuildPopupContext(callbackPopup, out popupContext))
            {
                var callbackPresent = HasOnSetGameCallback(popupContext.Popup);
                var active = IsPopupActive(popupContext.Popup);
                var sceneLoaded = IsPopupSceneLoaded(popupContext.Popup);
                if (callbackPresent && active && sceneLoaded)
                {
                    _log.Info("EXEC", $"Using popup with known onSetGame callback from {callbackSource} capturedAt={callbackCapturedUtc:O}: {ReflectionHelper.DescribeObjectIdentity(popupContext.Popup)} active={active} callback={callbackPresent}");
                    popupAcquireStatus = PopupAcquireStatus.Acquired;
                    return true;
                }

                _log.Warn("EXEC", $"Discarding cached callback popup from {callbackSource} because it is no longer usable: {ReflectionHelper.DescribeObjectIdentity(popupContext.Popup)} active={active} callback={callbackPresent} sceneLoaded={sceneLoaded}");
                MultiplayerRuntimeState.ClearLatestPopupObject();
                popupContext = default;
                if (!active || !sceneLoaded)
                    popupAcquireStatus = PopupAcquireStatus.WaitingForActivePopup;
            }
            else
            {
                _log.Warn("EXEC", $"Discarding cached callback popup from {callbackSource} because its popup context could not be rebuilt: {ReflectionHelper.DescribeObjectIdentity(callbackPopup)}");
                MultiplayerRuntimeState.ClearLatestPopupObject();
            }
        }

        if (MultiplayerRuntimeState.TryGetLatestPopupWithSetGameCallback(out _, out var cachedCallback, out _, out _) &&
            cachedCallback != null)
        {
            _log.Info("EXEC", $"A cached onSetGame delegate is available. fallbackEnabled={_config.EnableCachedSetGameCallbackFallback.Value}");
        }

        var controllerCandidates = GetControllerCandidates().ToList();
        if (controllerCandidates.Count == 0)
            _log.Warn("EXEC", "No live controller candidates were found for popup acquisition.");

        foreach (var controllerCandidate in controllerCandidates)
        {
            var beforeIds = new HashSet<int>(ReflectionHelper.GetLiveObjects(popupType).Select(GetInstanceId));
            var openMethod = ReflectionHelper.FindMethod(controllerCandidate.Controller.GetType(), controllerCandidate.OpenMethodName, 0);
            if (openMethod == null)
                continue;

            _log.Info("EXEC", $"Opening popup via {controllerCandidate.Controller.GetType().FullName}.{controllerCandidate.OpenMethodName} on {ReflectionHelper.DescribeObjectIdentity(controllerCandidate.Controller)}");
            openMethod.Invoke(controllerCandidate.Controller, Array.Empty<object>());

            var visiblePopups = ReflectionHelper.GetLiveObjects(popupType).ToList();
            var popup = SelectActivePopupCandidate(visiblePopups, beforeIds);

            if (popup != null && TryBuildPopupContext(popup, out popupContext))
            {
                popupContext.Controller = controllerCandidate.Controller;
                popupContext.ControllerMethod = controllerCandidate.OpenMethodName;
                _log.Info("EXEC", $"Using popup from controller flow {ReflectionHelper.DescribeObjectIdentity(popupContext.Popup)} active={IsPopupActive(popupContext.Popup)}");
                popupAcquireStatus = PopupAcquireStatus.Acquired;
                return true;
            }

            if (visiblePopups.Any())
                popupAcquireStatus = PopupAcquireStatus.WaitingForActivePopup;
        }

        var existingPopups = ReflectionHelper.GetLiveObjects(popupType).ToList();
        var fallbackPopup = SelectActivePopupCandidate(existingPopups, beforeIds: null);
        if (fallbackPopup != null && TryBuildPopupContext(fallbackPopup, out popupContext))
        {
            _log.Info("EXEC", $"Reusing existing popup {ReflectionHelper.DescribeObjectIdentity(popupContext.Popup)} active={IsPopupActive(popupContext.Popup)} callback={HasOnSetGameCallback(popupContext.Popup)}");
            popupAcquireStatus = PopupAcquireStatus.Acquired;
            return true;
        }

        if (existingPopups.Any())
        {
            _log.Warn("EXEC", $"Only inactive popup instances were available after controller attempts. count={existingPopups.Count}");
            popupAcquireStatus = PopupAcquireStatus.WaitingForActivePopup;
        }

        return false;
    }

    private static object? SelectActivePopupCandidate(IEnumerable<object> popupCandidates, ISet<int>? beforeIds)
    {
        return popupCandidates.FirstOrDefault(candidate => IsEligiblePopupCandidate(candidate, beforeIds, requireCallback: true))
               ?? popupCandidates.FirstOrDefault(candidate => IsEligiblePopupCandidate(candidate, beforeIds, requireCallback: false));
    }

    private static bool IsEligiblePopupCandidate(object candidate, ISet<int>? beforeIds, bool requireCallback)
    {
        if (candidate == null || !IsPopupActive(candidate))
            return false;

        if (beforeIds != null && beforeIds.Contains(GetInstanceId(candidate)))
            return false;

        return !requireCallback || HasOnSetGameCallback(candidate);
    }

    /// <summary>
    /// Force-initializes BatterySpecificationToggle backing fields on the popup's GameObject.
    /// Liftoff's BatterySpecificationToggle.ActiveModifier accesses a private 'toggle' field
    /// directly, but it is only lazy-initialized via the public 'Toggle' property getter.
    /// After a disconnect recovery the popup's toggles may not have been initialized, causing
    /// NullReferenceException in OnSetGame → DroneSettingsPanel.ApplyToGameSettings.
    /// </summary>
    private void PreInitBatterySpecificationToggles(PopupContext context)
    {
        PreInitBatterySpecificationToggles(context.Popup);
    }

    private void PreInitBatterySpecificationToggles(object popup)
    {
        try
        {
            var popupGo = (popup as Component)?.gameObject;
            if (popupGo == null) return;

            var bstType = _discovery.TryResolveKnownType("BatterySpecificationToggle");
            if (bstType == null) return;

            var components = popupGo.GetComponentsInChildren(bstType, true);
            if (components.Length == 0) return;

            var toggleProp = bstType.GetProperty("Toggle", BindingFlags.Instance | BindingFlags.Public);
            if (toggleProp == null) return;

            foreach (var comp in components)
                toggleProp.GetValue(comp); // Force lazy init of backing 'toggle' field

            _log.Info("CREATE", $"Pre-initialized {components.Length} BatterySpecificationToggle instances.");
        }
        catch (Exception ex)
        {
            _log.Warn("CREATE", $"BatterySpecificationToggle pre-init failed (non-fatal): {ex.Message}");
        }
    }

    private void EnsurePopupPrepared(PopupContext context)
    {
        if (HasSelectableContent(context))
            return;

        _log.Info("EXEC", "Popup has no selectable content yet. Attempting to repopulate from live session settings.");
        var desiredGameMode = GetRoomGameModeSnapshot();
        var desiredSlipstream = GetRoomSlipstreamSnapshot();

        if (TryApplyLiveSessionSettings(context))
            _log.Info("EXEC", "Reapplied live session settings to popup/content panel.");

        InvokeZeroArg(context.ContentPanel, "InitializeDefault");
        InvokeZeroArg(context.ContentPanel, "FillGameModeSelection");
        ApplyRoomSnapshotToContentPanel(context, desiredGameMode, desiredSlipstream);
        if (desiredGameMode != null)
            TryInvokeSelectionMethod(context.ContentPanel, "OnGameModeSelected", desiredGameMode);
        InvokeZeroArg(context.ContentPanel, "FillEnvironmentSelection");
        InvokeZeroArg(context.ContentPanel, "FillContentSelection");
        InvokeZeroArg(context.ContentPanel, "InvokeCurrentValues");

        _log.Info("EXEC", $"Post-prime selectable content: popupOptions={GetPopupOptionCount(context)} selectedContent={GetSelectedContentCount(context)}");
    }

    private void ApplyRoomSnapshotToContentPanel(PopupContext context, object? desiredGameMode, bool? desiredSlipstream)
    {
        if (desiredGameMode != null)
        {
            try
            {
                ReflectionHelper.SetMemberValue(context.ContentPanel, "SelectedGameMode", desiredGameMode);
                _log.Info("EXEC", $"Applied room game mode snapshot -> {desiredGameMode}");
            }
            catch (Exception ex)
            {
                _log.Warn("EXEC", $"Failed to apply room game mode snapshot: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (desiredSlipstream.HasValue)
        {
            try
            {
                ReflectionHelper.SetMemberValue(context.ContentPanel, "IncludeSlipstreamAssets", desiredSlipstream.Value);
                _log.Info("EXEC", $"Applied room slipstream snapshot -> {desiredSlipstream.Value}");
            }
            catch (Exception ex)
            {
                _log.Warn("EXEC", $"Failed to apply room slipstream snapshot: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private IEnumerable<ControllerCandidate> GetControllerCandidates()
    {
        // In-game controllers — expose the multiplayer game-settings popup while the
        // host is loaded INTO a race (in-game menu / race score panel). Restored for
        // JmtLiftoffMod: this bot sits in the race scene as host, so it must be
        // able to change tracks from the in-game screen without returning to the
        // lobby waiting room. Tried first; the lobby path below remains as a fallback
        // for when the bot is back in the waiting room.
        foreach (var mapping in new[]
                 {
                     new ControllerCandidate("MultiplayerRaceScoreButtonPanel", "OnGameSettings"),
                     new ControllerCandidate("InGameMenuMainPanel", "OnMultiplayerGameSettings"),
                 })
        {
            var type = _discovery.TryResolveKnownType(mapping.TypeName);
            if (type == null)
                continue;
            foreach (var controller in ReflectionHelper.GetLiveObjects(type))
            {
                _log.Info("EXEC", $"Found in-game controller: {type.FullName}.{mapping.OpenMethodName} on {ReflectionHelper.DescribeObjectIdentity(controller)}");
                yield return mapping.WithController(controller);
            }
        }

        // Lobby controller — find types that hold the popup prefab (works in the lobby waiting room)
        foreach (var lobbyControllerCandidate in GetLobbyControllerCandidates())
            yield return lobbyControllerCandidate;
    }

    /// <summary>
    /// Finds lobby controllers that have a prefabPopupMultiplayerSetup field.
    /// These controllers can instantiate the track-change popup from the lobby waiting room.
    /// </summary>
    private IEnumerable<ControllerCandidate> GetLobbyControllerCandidates()
    {
        var lobbyTypes = _discovery.FindTypesByFieldName("prefabPopupMultiplayerSetup");
        foreach (var type in lobbyTypes)
        {
            // Look for a method that opens/shows the game settings popup
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            string? openMethodName = null;

            // Try common method patterns for opening the game settings popup
            foreach (var methodName in new[] { "OnGameSettings", "OnCreateRoom", "ShowGameSettings", "OpenGameSettings" })
            {
                var method = methods.FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 0);
                if (method != null)
                {
                    openMethodName = method.Name;
                    break;
                }
            }

            // Fallback: look for any zero-arg method that references "popup" or "setup" in name
            if (openMethodName == null)
            {
                var candidateMethod = methods.FirstOrDefault(m =>
                    m.GetParameters().Length == 0 &&
                    !m.IsSpecialName &&
                    m.DeclaringType == type &&
                    (m.Name.IndexOf("popup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     m.Name.IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     m.Name.IndexOf("game", StringComparison.OrdinalIgnoreCase) >= 0));

                if (candidateMethod != null)
                    openMethodName = candidateMethod.Name;
            }

            foreach (var controller in ReflectionHelper.GetLiveObjects(type))
            {
                if (openMethodName != null)
                {
                    _log.Info("EXEC", $"Found lobby controller: {type.FullName}.{openMethodName} on {ReflectionHelper.DescribeObjectIdentity(controller)}");
                    yield return new ControllerCandidate(type.FullName ?? type.Name, openMethodName).WithController(controller);
                }
                else
                {
                    // No known open method, but we can try direct popup instantiation
                    _log.Info("EXEC", $"Found lobby controller with popup prefab but no open method: {type.FullName} on {ReflectionHelper.DescribeObjectIdentity(controller)}");
                    if (TryDirectPopupInstantiation(type, controller))
                    {
                        // The popup was instantiated directly — it should now be findable by the normal acquisition flow
                        _log.Info("EXEC", "Direct popup instantiation from lobby controller succeeded.");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Attempts to instantiate the popup directly from a lobby controller's prefab field.
    /// This mirrors what the game does: Instantiate(prefab), InitializeDefault(), ApplyFromGameSettings().
    /// Also hooks the onSetGame callback by finding the lobby controller's handler method.
    /// </summary>
    private bool TryDirectPopupInstantiation(Type controllerType, object controller)
    {
        try
        {
            var prefabField = controllerType.GetField("prefabPopupMultiplayerSetup",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prefabField == null) return false;

            var prefab = prefabField.GetValue(controller);
            if (prefab == null)
            {
                _log.Warn("EXEC", "prefabPopupMultiplayerSetup is null on live lobby controller.");
                return false;
            }

            _log.Info("EXEC", $"Instantiating popup from prefab: {ReflectionHelper.DescribeObjectIdentity(prefab)}");

            // Instantiate the prefab
            var popupInstance = UnityEngine.Object.Instantiate(prefab as UnityEngine.Object);
            if (popupInstance == null)
            {
                _log.Warn("EXEC", "UnityEngine.Object.Instantiate returned null for popup prefab.");
                return false;
            }

            _log.Info("EXEC", $"Popup instantiated: {ReflectionHelper.DescribeObjectIdentity(popupInstance)}");

            // Try InitializeDefault
            var initMethod = ReflectionHelper.FindMethod(popupInstance.GetType(), "InitializeDefault", 0);
            if (initMethod != null)
            {
                _log.Info("EXEC", "Calling InitializeDefault on instantiated popup.");
                initMethod.Invoke(popupInstance, Array.Empty<object>());
            }

            // Hook the onSetGame callback to the lobby controller's handler
            TryHookLobbyOnSetGameCallback(controllerType, controller, popupInstance);

            // Store this as the lobby controller's popup reference
            // Prefer the waiting room host controller (has panelWaitingRoomMenu)
            var isWaitingRoomHost = controllerType.GetField("panelWaitingRoomMenu",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;
            if (isWaitingRoomHost || _lastLobbyController == null)
            {
                _lastLobbyController = controller;
                _lastLobbyControllerType = controllerType;
                _log.Info("EXEC", $"Stored lobby controller: type={controllerType.Name} isWaitingRoomHost={isWaitingRoomHost}");
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.Error("EXEC", "Direct popup instantiation from lobby controller failed.", ex);
            return false;
        }
    }

    private object? _lastLobbyController;
    private Type? _lastLobbyControllerType;

    /// <summary>
    /// Builds a Hashtable from the popup's content panel selections and applies it
    /// to the Photon room by finding a (Hashtable, Boolean) method on the lobby controller.
    /// Falls back to PhotonNetwork.CurrentRoom.SetCustomProperties if no handler found.
    /// </summary>
    private bool TryApplyViaHashtable(PopupContext context, ChangeRequest request)
    {
        try
        {
            _log.Info("EXEC", "Lobby Hashtable path: building room properties from popup selections.");

            // Get the selected content from the popup's content panel
            var selectedContent = ReflectionHelper.GetMemberValue(context.ContentPanel, "SelectedContent");
            var selectedEnvironment = ReflectionHelper.GetMemberValue(context.ContentPanel, "SelectedEnvironment")
                                     ?? ReflectionHelper.GetMemberValue(context.ContentPanel, "SelectedEnvironmentName");
            var selectedGameMode = ReflectionHelper.GetMemberValue(context.ContentPanel, "SelectedGameMode");

            _log.Info("EXEC", $"Selected content={selectedContent != null} env={selectedEnvironment} gameMode={selectedGameMode}");

            // Build the Hashtable using the popup's ApplyToGameSettings
            // First, look for methods on the lobby controller that take (Hashtable, Boolean)
            var methods = _lastLobbyControllerType!.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            MethodInfo? hashtableHandler = null;
            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(Hashtable) &&
                    parameters[1].ParameterType == typeof(bool))
                {
                    hashtableHandler = method;
                    _log.Info("EXEC", $"Found Hashtable handler on lobby controller: {ReflectionHelper.FormatMethodSignature(method)}");
                    break;
                }
            }

            // Also check MultiplayerRaceScoreButtonPanel for OnGameSettingsUpdated
            if (hashtableHandler == null)
            {
                var scoreType = _discovery.TryResolveKnownType("MultiplayerRaceScoreButtonPanel");
                if (scoreType != null)
                {
                    var scoreHandler = ReflectionHelper.FindMethod(scoreType, "OnGameSettingsUpdated", 2);
                    if (scoreHandler != null)
                    {
                        var liveScorePanels = ReflectionHelper.GetLiveObjects(scoreType);
                        if (liveScorePanels.Count > 0)
                        {
                            _log.Info("EXEC", "Found MultiplayerRaceScoreButtonPanel.OnGameSettingsUpdated — but may not be live in lobby.");
                        }
                    }
                }
            }

            // Now build the Hashtable from the popup by calling OnSetGame and intercepting
            // the resulting settings. Actually, let's just build it from known room property keys.
            // The room properties use keys like "E" (environment), custom content props, etc.
            // We can read the current room properties, modify the track/race, and set them back.

            var room = PhotonNetwork.CurrentRoom;
            if (room == null)
            {
                _log.Warn("EXEC", "Not in a Photon room — cannot set room properties.");
                return false;
            }

            // Get the popup's ApplyToGameSettings result by using it on a LastMultiplayerSession
            var lastSessionType = _discovery.TryResolveKnownType("Liftoff.Multiplayer.GameSetup.LastMultiplayerSession");
            if (lastSessionType == null)
            {
                _log.Warn("EXEC", "LastMultiplayerSession type not found.");
                return false;
            }

            var lastSessions = ReflectionHelper.GetLiveObjects(lastSessionType);
            if (lastSessions.Count == 0)
            {
                _log.Warn("EXEC", "No live LastMultiplayerSession instances.");
                return false;
            }

            var lastSession = lastSessions[0];

            // Apply the popup's selections to the LastMultiplayerSession
            var applyTo = ReflectionHelper.FindMethod(context.Popup.GetType(), "ApplyToGameSettings", 1);
            if (applyTo != null)
            {
                // Check if LastMultiplayerSession is assignable to the parameter type
                var paramType = applyTo.GetParameters()[0].ParameterType;
                if (paramType.IsInstanceOfType(lastSession))
                {
                    applyTo.Invoke(context.Popup, new[] { lastSession });
                    _log.Info("EXEC", $"Applied popup selections to LastMultiplayerSession.");

                    // Now the LastMultiplayerSession has the updated track/race.
                    // Call the lobby controller's handler if we found one
                    if (hashtableHandler != null)
                    {
                        // Build a Hashtable from the session - try to find a ToHashtable-like method
                        // on LastMultiplayerSession or just use the lobby controller handler directly
                        // with a settings object instead
                    }

                    // Direct approach: the lobby controller should have a method that takes
                    // the game properties interface. LastMultiplayerSession implements it.
                    // Find method on lobby controller that accepts the interface
                    foreach (var method in methods)
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(lastSession))
                        {
                            _log.Info("EXEC", $"Calling lobby controller with LastMultiplayerSession: {ReflectionHelper.FormatMethodSignature(method)}");
                            method.Invoke(_lastLobbyController, new[] { lastSession });
                            _log.Info("EXEC", "Lobby controller handler invoked with updated session.");
                            return true;
                        }
                    }

                    // Direct Photon: just reload the room with the updated settings
                    // by finding the "LoadLevel" equivalent
                    foreach (var method in methods)
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length == 2 &&
                            parameters[0].ParameterType.IsInstanceOfType(lastSession))
                        {
                            _log.Info("EXEC", $"Calling lobby controller 2-param with session: {ReflectionHelper.FormatMethodSignature(method)}");
                            object? secondParam = null;
                            try
                            {
                                secondParam = parameters[1].ParameterType == typeof(bool) ? (object)false : Activator.CreateInstance(parameters[1].ParameterType);
                            }
                            catch { }
                            method.Invoke(_lastLobbyController, new[] { lastSession, secondParam });
                            _log.Info("EXEC", "Lobby controller handler invoked.");
                            return true;
                        }
                    }

                    _log.Warn("EXEC", "No lobby controller method accepts LastMultiplayerSession. Listing candidates:");
                    foreach (var method in methods)
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length >= 1 && parameters.Length <= 2)
                        {
                            var paramTypes = string.Join(", ", parameters.Select(p => p.ParameterType.Name));
                            _log.Warn("EXEC", $"  {method.Name}({paramTypes}) returns {method.ReturnType.Name}");
                        }
                    }
                }
                else
                {
                    _log.Warn("EXEC", $"LastMultiplayerSession ({lastSession.GetType().Name}) is not assignable to {paramType.Name}");
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _log.Error("EXEC", "TryApplyViaHashtable failed.", ex);
            return false;
        }
    }

    /// <summary>
    /// Calls the lobby controller's (Hashtable, Boolean) handler directly.
    /// Builds the Hashtable by calling ApplyToGameSettings on the popup to update
    /// a temporary settings object, then converting it to room properties.
    /// </summary>
    private bool TryApplyViaHashtableDirectly(PopupContext context)
    {
        try
        {
            // Find the (Hashtable, Boolean) method on the lobby controller
            var methods = _lastLobbyControllerType!.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            MethodInfo? hashtableHandler = null;
            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(Hashtable) &&
                    parameters[1].ParameterType == typeof(bool))
                {
                    hashtableHandler = method;
                    break;
                }
            }

            if (hashtableHandler == null)
            {
                _log.Warn("EXEC", "No (Hashtable, Boolean) method found on lobby controller.");
                return false;
            }

            _log.Info("EXEC", $"Using Hashtable handler: {ReflectionHelper.FormatMethodSignature(hashtableHandler)}");

            // We need to build a Hashtable with the updated game settings.
            // The popup has already been configured with the desired track/race.
            // Call OnSetGame internally to generate the settings, then intercept them.
            //
            // Approach: call ApplyToGameSettings on the popup to fill a settings object,
            // then use that object's properties to build a Hashtable matching the room format.
            //
            // Simpler approach: read the popup's selected content directly and build
            // the Hashtable from the known Photon room property keys.

            var room = PhotonNetwork.CurrentRoom;
            if (room?.CustomProperties == null)
            {
                _log.Warn("EXEC", "Not in a Photon room.");
                return false;
            }

            // Clone the current room properties and update track/race
            var updatedProps = new Hashtable();
            foreach (System.Collections.DictionaryEntry entry in room.CustomProperties)
            {
                updatedProps[entry.Key] = entry.Value;
            }

            // Get the selected track and race from the popup's content panel
            var selectedContent = ReflectionHelper.GetMemberValue(context.ContentPanel, "SelectedContent");
            var allContent = ReflectionHelper.GetMemberValue(context.ContentPanel, "currentContent")
                             ?? ReflectionHelper.GetMemberValue(context.ContentPanel, "availableContent");

            // Try to get the track/race from the popup's dropdown selection
            var dropdown = context.Dropdown;
            var selectedIndex = ReflectionHelper.GetMemberValue(dropdown, "value")
                                ?? ReflectionHelper.GetMemberValue(dropdown, "Value");

            _log.Info("EXEC", $"Dropdown selected index: {selectedIndex}, selectedContent: {selectedContent != null}");

            // Read the selected track/race from the dropdown's Data property
            if (context.Dropdown is Dropdown selectedDropdown && selectedIndex is int selIdx && selIdx >= 0 && selIdx < selectedDropdown.options.Count)
            {
                var selectedOption = selectedDropdown.options[selIdx];
                var selectedData = ReflectionHelper.GetMemberValue(selectedOption, "Data")
                                   ?? ReflectionHelper.GetMemberValue(selectedOption, "data");

                if (selectedData != null)
                {
                    // The data is a GameContentEntry or similar — it has Track and Race sub-objects
                    // or it IS the track/race content entry itself
                    var contentTrack = ReflectionHelper.GetMemberValue(selectedData, "Track") ?? selectedData;
                    var contentRace = ReflectionHelper.GetMemberValue(selectedData, "Race") ?? selectedData;
                    var contentName = ReflectionHelper.GetMemberValue(selectedData, "Name") as string;

                    _log.Info("EXEC", $"Dropdown data: type={selectedData.GetType().Name} name={contentName} track={contentTrack != null} race={contentRace != null}");

                    // Check ContentType to determine if it's a track or race
                    var contentType = ReflectionHelper.GetMemberValue(selectedData, "ContentType");
                    _log.Info("EXEC", $"ContentType={contentType}");

                    // Update room properties with the selected content
                    // The dropdown data IS the race/track GameContentEntry
                    updatedProps["R"] = selectedData; // Race content entry
                    if (contentType?.ToString()?.Contains("1") == true)
                    {
                        updatedProps["T"] = selectedData; // Track content entry
                    }

                    _log.Info("EXEC", $"Updated Hashtable with selected content: R={selectedData.GetType().Name}");
                }
                else
                {
                    _log.Warn("EXEC", "Dropdown option has no Data property.");
                }
            }

            // Get the live session settings and apply the popup's selections to it
            var liveSettings = GetLiveSessionSettings();
            var applyTo = ReflectionHelper.FindMethod(context.Popup.GetType(), "ApplyToGameSettings", 1);
            if (liveSettings != null && applyTo != null)
            {
                var paramType = applyTo.GetParameters()[0].ParameterType;
                _log.Info("EXEC", $"Live settings type: {liveSettings.GetType().Name}, ApplyToGameSettings param type: {paramType.Name}, assignable: {paramType.IsInstanceOfType(liveSettings)}");

                if (paramType.IsInstanceOfType(liveSettings))
                {
                    applyTo.Invoke(context.Popup, new[] { liveSettings });
                    _log.Info("EXEC", "Applied popup selections to live session settings.");

                    // Now set the updated settings as Photon room custom properties
                    // The settings should have a method to convert to room properties
                    var settingsType = liveSettings.GetType();
                    var allMethods = settingsType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    // Look for any method returning Hashtable
                    foreach (var m in allMethods)
                    {
                        if (typeof(Hashtable).IsAssignableFrom(m.ReturnType) && m.GetParameters().Length == 0)
                        {
                            var ht = m.Invoke(liveSettings, Array.Empty<object>()) as Hashtable;
                            if (ht != null && ht.Count > 0)
                            {
                                _log.Info("EXEC", $"Got Hashtable from settings via {m.Name}: {ht.Count} keys");
                                hashtableHandler.Invoke(_lastLobbyController, new object[] { ht, true });
                                _log.Info("EXEC", "Applied settings via Hashtable handler.");
                                return true;
                            }
                        }
                    }

                    // Direct: pass the updated settings to the lobby controller handler
                    var controllerMethods = _lastLobbyControllerType!.GetMethods(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (var m in controllerMethods)
                    {
                        var parameters = m.GetParameters();
                        if (parameters.Length == 2 &&
                            parameters[0].ParameterType.IsInstanceOfType(liveSettings) &&
                            parameters[1].ParameterType == typeof(bool))
                        {
                            _log.Info("EXEC", $"Calling lobby controller (settings, bool): {ReflectionHelper.FormatMethodSignature(m)}");
                            m.Invoke(_lastLobbyController, new[] { liveSettings, (object)true });
                            _log.Info("EXEC", "Lobby controller (settings, bool) invoked.");
                            return true;
                        }
                    }
                }
            }
            else
            {
                _log.Warn("EXEC", $"Live settings: {liveSettings != null}, ApplyToGameSettings: {applyTo != null}");
            }

            // Read the selected content from the content panel
            // The content panel tracks the selected environment, track, and race
            var panelTrack = ReflectionHelper.GetMemberValue(context.ContentPanel, "SelectedTrack")
                             ?? ReflectionHelper.GetMemberValue(context.ContentPanel, "selectedTrack");
            var panelRace = ReflectionHelper.GetMemberValue(context.ContentPanel, "SelectedRace")
                            ?? ReflectionHelper.GetMemberValue(context.ContentPanel, "selectedRace");
            var panelEnv = ReflectionHelper.GetMemberValue(context.ContentPanel, "SelectedEnvironment")
                           ?? ReflectionHelper.GetMemberValue(context.ContentPanel, "SelectedEnvironmentName")
                           ?? ReflectionHelper.GetMemberValue(context.ContentPanel, "selectedEnvironment");
            var panelGameMode = ReflectionHelper.GetMemberValue(context.ContentPanel, "SelectedGameMode")
                                ?? ReflectionHelper.GetMemberValue(context.ContentPanel, "selectedGameMode");
            var panelSlipstream = ReflectionHelper.GetMemberValue(context.ContentPanel, "IncludeSlipstreamAssets");

            _log.Info("EXEC", $"Panel selections: env={panelEnv} track={panelTrack != null} race={panelRace != null} gameMode={panelGameMode}");

            // Dump all fields on ContentPanel to find the right property names
            if (panelTrack == null && panelRace == null)
            {
                _log.Info("EXEC", "ContentPanel fields:");
                var cpFields = context.ContentPanel.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var f in cpFields)
                {
                    var val = f.GetValue(context.ContentPanel);
                    var hasVal = val != null && !(val is string s && s.Length == 0);
                    if (hasVal)
                        _log.Info("EXEC", $"  {f.FieldType.Name} {f.Name} = {(val is string ? val : val?.GetType().Name ?? "null")}");
                }
                var cpProps = context.ContentPanel.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var p in cpProps)
                {
                    try
                    {
                        var val = p.GetValue(context.ContentPanel);
                        if (val != null)
                            _log.Info("EXEC", $"  prop {p.PropertyType.Name} {p.Name} = {(val is string ? val : val?.GetType().Name ?? "null")}");
                    }
                    catch { }
                }
            }

            if (panelTrack != null) updatedProps["T"] = panelTrack;
            if (panelRace != null) updatedProps["R"] = panelRace;
            if (panelEnv is string envStr && envStr.Length > 0) updatedProps["E"] = envStr;
            if (panelGameMode != null) updatedProps["GM"] = panelGameMode;

            // Call the handler
            _log.Info("EXEC", $"Calling lobby controller Hashtable handler with {updatedProps.Count} properties, returnToWaitingRoom=true");
            hashtableHandler.Invoke(_lastLobbyController, new object[] { updatedProps, true });
            _log.Info("EXEC", "Lobby controller Hashtable handler invoked successfully.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("EXEC", "TryApplyViaHashtableDirectly failed.", ex);
            return false;
        }
    }

    /// <summary>
    /// Uses the game's own "Change Settings" button flow from the lobby waiting room.
    /// 1. Find the panelWaitingRoomMenu on the lobby controller
    /// 2. Find a Button that opens the game settings popup
    /// 3. Click it (game hooks the callback properly)
    /// 4. Configure the new popup with our desired track
    /// 5. Call OnSetGame on it to auto-confirm
    /// </summary>
    private bool TryApplyViaChangeSettingsButton(ChangeRequest request)
    {
        try
        {
            if (_lastLobbyController == null || _lastLobbyControllerType == null)
                return false;

            _log.Info("EXEC", "Lobby path: looking for Change Settings button in waiting room.");

            // Find all Button fields on the lobby controller
            var fields = _lastLobbyControllerType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var popupType = _discovery.TryResolveKnownType("Liftoff.Multiplayer.GameSetup.PopupQuickPlayMultiplayerSetup");

            // Track popups before clicking anything
            var beforePopups = popupType != null
                ? new HashSet<int>(ReflectionHelper.GetLiveObjects(popupType).Select(GetInstanceId))
                : new HashSet<int>();

            // Look for Button fields on the panelWaitingRoomMenu
            var waitingRoomMenuField = fields.FirstOrDefault(f => f.Name == "panelWaitingRoomMenu");
            object? menuPanel = waitingRoomMenuField?.GetValue(_lastLobbyController);

            if (menuPanel == null)
            {
                _log.Warn("EXEC", "panelWaitingRoomMenu is null.");
                return false;
            }

            _log.Info("EXEC", $"panelWaitingRoomMenu found: {ReflectionHelper.DescribeObjectIdentity(menuPanel)}");

            // Find all Buttons in the waiting room menu panel
            var buttons = new List<(string name, Button button)>();
            if (menuPanel is Component menuComponent)
            {
                var allButtons = menuComponent.GetComponentsInChildren<Button>(true);
                foreach (var btn in allButtons)
                {
                    var btnName = btn.gameObject.name;
                    _log.Info("EXEC", $"  Button: name=\"{btnName}\" interactable={btn.interactable}");
                    buttons.Add((btnName, btn));
                }
            }

            // Find the "Change Room Settings" button specifically
            var settingsButton = buttons.FirstOrDefault(b =>
                b.name.IndexOf("ChangeRoomSettings", StringComparison.OrdinalIgnoreCase) >= 0 ||
                b.name.IndexOf("ChangeSetting", StringComparison.OrdinalIgnoreCase) >= 0 ||
                b.name.IndexOf("GameSettings", StringComparison.OrdinalIgnoreCase) >= 0 ||
                b.name.IndexOf("RoomSettings", StringComparison.OrdinalIgnoreCase) >= 0).button;

            if (settingsButton == null && buttons.Count > 0)
            {
                // Just try the first interactable button as a fallback diagnostic
                _log.Warn("EXEC", "No 'settings' button found by name. Available buttons listed above.");
                return false;
            }

            if (settingsButton == null)
            {
                _log.Warn("EXEC", "No buttons found in panelWaitingRoomMenu.");
                return false;
            }

            _log.Info("EXEC", $"Clicking settings button: \"{settingsButton.gameObject.name}\"");
            settingsButton.onClick.Invoke();

            // Find the newly spawned popup (with callback properly hooked by the game)
            if (popupType == null) return false;

            var afterPopups = ReflectionHelper.GetLiveObjects(popupType);
            object? newPopup = null;
            foreach (var popup in afterPopups)
            {
                if (!beforePopups.Contains(GetInstanceId(popup)) && IsPopupActive(popup))
                {
                    newPopup = popup;
                    break;
                }
            }

            // Also check existing popups that might have been reactivated
            if (newPopup == null)
            {
                foreach (var popup in afterPopups)
                {
                    if (IsPopupActive(popup) && HasOnSetGameCallback(popup))
                    {
                        newPopup = popup;
                        break;
                    }
                }
            }

            if (newPopup == null)
            {
                _log.Warn("EXEC", $"No popup appeared after clicking settings button. before={beforePopups.Count} after={afterPopups.Count}");
                return false;
            }

            _log.Info("EXEC", $"Settings popup opened by game: {ReflectionHelper.DescribeObjectIdentity(newPopup)} callback={HasOnSetGameCallback(newPopup)}");

            // Build popup context and configure it with our desired track
            if (!TryBuildPopupContext(newPopup, out var dialogContext))
            {
                _log.Warn("EXEC", "Could not build popup context for game-opened popup.");
                return false;
            }

            EnsurePopupPrepared(dialogContext);

            if (!ConfigurePopupForRequest(dialogContext, request, out var selectionSummary))
            {
                _log.Warn("EXEC", "Could not configure popup for request.");
                return false;
            }

            _log.Info("EXEC", $"Popup configured: {selectionSummary}");

            // Auto-click "Set Game"
            PreInitBatterySpecificationToggles(newPopup);
            var onSetGame = ReflectionHelper.FindMethod(newPopup.GetType(), "OnSetGame", 0);
            if (onSetGame == null)
            {
                _log.Warn("EXEC", "OnSetGame not found on game-opened popup.");
                return false;
            }

            _log.Info("EXEC", "Calling OnSetGame on game-opened popup (callback should propagate).");
            onSetGame.Invoke(newPopup, Array.Empty<object>());
            _log.Info("EXEC", "Track change applied via Change Settings button flow.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("EXEC", "TryApplyViaChangeSettingsButton failed.", ex);
            return false;
        }
    }

    private void DestroyLobbyPopup(object popup)
    {
        try
        {
            if (popup is UnityEngine.Object unityObj && unityObj != null)
            {
                UnityEngine.Object.Destroy(unityObj);
                _log.Info("EXEC", "Destroyed lobby-instantiated popup.");
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Applies the configured track change by building a settings payload from the popup
    /// and setting the Photon room custom properties directly.
    /// This is used when we're in the lobby waiting room and don't have the normal
    /// in-game controller flow available.
    /// </summary>
    private bool TryApplyViaLobbyController(PopupContext context)
    {
        try
        {
            // Build the settings payload from the popup.
            // The settings type has no default constructor, so we get an existing instance
            // from the popup's ApplyToGameSettings (0-arg overload that returns the settings)
            // or from the lobby controller's cached settings.
            var applyToGameSettings1 = ReflectionHelper.FindMethod(context.Popup.GetType(), "ApplyToGameSettings", 1);
            var applyToGameSettings0 = ReflectionHelper.FindMethod(context.Popup.GetType(), "ApplyToGameSettings", 0);

            Type? settingsType = null;
            object? settingsPayload = null;

            // Try 0-arg version that returns a settings object
            if (applyToGameSettings0 != null && applyToGameSettings0.ReturnType != typeof(void))
            {
                settingsPayload = applyToGameSettings0.Invoke(context.Popup, Array.Empty<object>());
                settingsType = applyToGameSettings0.ReturnType;
                _log.Info("EXEC", $"Got settings via ApplyToGameSettings() return: type={settingsType.Name}");
            }

            // Try 1-arg version: need to get or create the settings object first
            if (settingsPayload == null && applyToGameSettings1 != null)
            {
                settingsType = applyToGameSettings1.GetParameters()[0].ParameterType;

                // Try to get existing settings from the host state detector
                var host = _hostDetector.Capture();
                if (host.SessionSettings != null && host.SessionSettings.GetType() == settingsType)
                {
                    settingsPayload = host.SessionSettings;
                    _log.Info("EXEC", $"Using existing SessionSettings as settings payload.");
                }

                // Try to get from LastMultiplayerSession
                if (settingsPayload == null)
                {
                    var lastSessionType = _discovery.TryResolveKnownType("Liftoff.Multiplayer.GameSetup.LastMultiplayerSession");
                    if (lastSessionType != null)
                    {
                        var lastSessions = ReflectionHelper.GetLiveObjects(lastSessionType);
                        if (lastSessions.Count > 0)
                        {
                            // LastMultiplayerSession implements the settings interface - try to use it
                            settingsPayload = lastSessions[0];
                            _log.Info("EXEC", $"Using LastMultiplayerSession as base settings.");
                        }
                    }
                }

                // Try constructors with parameters
                if (settingsPayload == null)
                {
                    var constructors = settingsType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (var ctor in constructors)
                    {
                        var ctorParams = ctor.GetParameters();
                        if (ctorParams.Length == 0)
                        {
                            settingsPayload = ctor.Invoke(Array.Empty<object>());
                            break;
                        }
                        // Try constructor that takes a single IReadOnlyGameProperties or similar
                        if (ctorParams.Length == 1)
                        {
                            var lastSessionType = _discovery.TryResolveKnownType("Liftoff.Multiplayer.GameSetup.LastMultiplayerSession");
                            if (lastSessionType != null && ctorParams[0].ParameterType.IsAssignableFrom(lastSessionType))
                            {
                                var lastSessions = ReflectionHelper.GetLiveObjects(lastSessionType);
                                if (lastSessions.Count > 0)
                                {
                                    settingsPayload = ctor.Invoke(new[] { lastSessions[0] });
                                    _log.Info("EXEC", $"Created settings via constructor({ctorParams[0].ParameterType.Name})");
                                    break;
                                }
                            }
                        }
                    }
                }

                if (settingsPayload != null && applyToGameSettings1 != null)
                {
                    // Fill the payload with the popup's configured values
                    applyToGameSettings1.Invoke(context.Popup, new[] { settingsPayload });
                    _log.Info("EXEC", $"Applied popup selections to settings payload.");
                }
            }

            if (settingsPayload == null || settingsType == null)
            {
                // Diagnostic: dump what we found
                _log.Warn("EXEC", $"Could not obtain settings payload. applyTo0={applyToGameSettings0 != null} applyTo1={applyToGameSettings1 != null} settingsType={settingsType?.FullName ?? "null"}");
                if (applyToGameSettings1 != null)
                {
                    var st = applyToGameSettings1.GetParameters()[0].ParameterType;
                    _log.Warn("EXEC", $"Settings type: {st.FullName}");
                    var ctors = st.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (var ctor in ctors)
                    {
                        var p = ctor.GetParameters();
                        _log.Warn("EXEC", $"  Constructor({string.Join(", ", p.Select(x => x.ParameterType.Name))})");
                    }

                    // Try ALL fields on the lobby controller to find one of the settings type
                    if (_lastLobbyController != null && _lastLobbyControllerType != null)
                    {
                        var fields = _lastLobbyControllerType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        foreach (var field in fields)
                        {
                            if (field.FieldType == st || st.IsAssignableFrom(field.FieldType))
                            {
                                var val = field.GetValue(_lastLobbyController);
                                _log.Info("EXEC", $"  Found matching field: {field.Name} type={field.FieldType.Name} value={val != null}");
                                if (val != null)
                                {
                                    settingsPayload = val;
                                    settingsType = st;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (settingsPayload == null)
                {
                    _log.Warn("EXEC", "Still could not obtain settings payload after field scan.");
                    return false;
                }
            }

            // Dump all methods on the settings type to find the right serialization path
            var allSettingsMethods = settingsType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            _log.Info("EXEC", $"Settings type {settingsType.Name} has {allSettingsMethods.Length} declared methods:");
            foreach (var m in allSettingsMethods.Take(20))
                _log.Info("EXEC", $"  method {ReflectionHelper.FormatMethodSignature(m)}");

            // Try to find any method that returns a Hashtable
            foreach (var m in settingsType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (typeof(ExitGames.Client.Photon.Hashtable).IsAssignableFrom(m.ReturnType) && m.GetParameters().Length == 0)
                {
                    _log.Info("EXEC", $"Found Hashtable-returning method: {ReflectionHelper.FormatMethodSignature(m)}");
                    var hashtable = m.Invoke(settingsPayload, Array.Empty<object>()) as ExitGames.Client.Photon.Hashtable;
                    if (hashtable != null && hashtable.Count > 0)
                    {
                        _log.Info("EXEC", $"Setting Photon room properties directly via {m.Name}: keys={hashtable.Count}");
                        PhotonNetwork.CurrentRoom.SetCustomProperties(hashtable);
                        return true;
                    }
                }
            }

            // Fallback: call the lobby controller's handler with the settings
            if (_lastLobbyController != null && _lastLobbyControllerType != null)
            {
                var methods = _lastLobbyControllerType.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                // Look for 2-param handler (settings, bool) — like LoadGameSettings
                foreach (var method in methods)
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length == 2 &&
                        parameters[0].ParameterType.IsAssignableFrom(settingsPayload.GetType()) &&
                        parameters[1].ParameterType == typeof(bool))
                    {
                        _log.Info("EXEC", $"Calling lobby controller 2-param handler: {ReflectionHelper.FormatMethodSignature(method)}");
                        method.Invoke(_lastLobbyController, new[] { settingsPayload, (object)true });
                        return true;
                    }
                }
            }

            // Use the lobby controller's handler directly.
            // First: fill the settings payload from the popup
            if (applyToGameSettings1 != null && settingsPayload != null)
            {
                applyToGameSettings1.Invoke(context.Popup, new[] { settingsPayload });
                _log.Info("EXEC", "Filled settings payload from popup.");
            }

            // Try calling the lobby controller's method that takes the settings
            if (_lastLobbyController != null && _lastLobbyControllerType != null && settingsPayload != null)
            {
                _log.Info("EXEC", $"Searching lobby controller {_lastLobbyControllerType.Name} for methods taking {settingsType.Name}");
                var methods = _lastLobbyControllerType.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                // Log ALL methods that accept the settings type
                var candidates = new List<MethodInfo>();
                foreach (var method in methods)
                {
                    var parameters = method.GetParameters();
                    if (parameters.Any(p => p.ParameterType == settingsType || settingsType.IsAssignableFrom(p.ParameterType)))
                    {
                        candidates.Add(method);
                        _log.Info("EXEC", $"  candidate: {ReflectionHelper.FormatMethodSignature(method)}");
                    }
                }

                // Try 2-param (settings, otherType) first — this is the actual apply method
                foreach (var method in candidates)
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length == 2 && parameters[0].ParameterType == settingsType)
                    {
                        _log.Info("EXEC", $"Calling lobby host 2-param method: {ReflectionHelper.FormatMethodSignature(method)}");
                        object? secondParam = null;
                        try { secondParam = Activator.CreateInstance(parameters[1].ParameterType); }
                        catch { /* try null */ }
                        method.Invoke(_lastLobbyController, new[] { settingsPayload, secondParam });
                        _log.Info("EXEC", "Lobby host handler invoked.");
                        return true;
                    }
                }

                // Try 1-param (settings)
                foreach (var method in candidates)
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == settingsType)
                    {
                        _log.Info("EXEC", $"Calling lobby host 1-param method: {ReflectionHelper.FormatMethodSignature(method)}");
                        method.Invoke(_lastLobbyController, new[] { settingsPayload });
                        _log.Info("EXEC", "Lobby host handler invoked.");
                        return true;
                    }
                }

                if (candidates.Count == 0)
                    _log.Warn("EXEC", "No methods on lobby controller accept the settings type.");
            }
            else
            {
                _log.Warn("EXEC", $"Lobby controller state: controller={_lastLobbyController != null} type={_lastLobbyControllerType != null} payload={settingsPayload != null}");
            }

            _log.Warn("EXEC", "No apply path found for lobby controller.");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error("EXEC", "TryApplyViaLobbyController failed.", ex);
            return false;
        }
    }

    /// <summary>
    /// Hooks the popup's onSetGame delegate to the lobby controller so that track changes
    /// propagate to the Photon room when OnSetGame() is called.
    /// </summary>
    private void TryHookLobbyOnSetGameCallback(Type controllerType, object controller, UnityEngine.Object popupInstance)
    {
        try
        {
            // Find the onSetGame event on the popup to determine the delegate type
            var onSetGameField = popupInstance.GetType().GetField("onSetGame",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (onSetGameField == null)
            {
                _log.Warn("EXEC", "Could not find onSetGame field on popup to hook callback.");
                return;
            }

            var delegateType = onSetGameField.FieldType;
            // delegateType is Action<T> where T is the game creation settings type
            var settingsType = delegateType.GenericTypeArguments.Length > 0
                ? delegateType.GenericTypeArguments[0]
                : null;

            if (settingsType == null)
            {
                _log.Warn("EXEC", $"onSetGame field type {delegateType.FullName} has no generic argument.");
                return;
            }

            _log.Info("EXEC", $"onSetGame delegate type: {delegateType.FullName}, settings type: {settingsType.FullName}");

            // Find a method on the lobby controller that accepts this settings type (single parameter)
            var controllerMethods = controllerType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            MethodInfo? handlerMethod = null;
            foreach (var method in controllerMethods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == settingsType)
                {
                    handlerMethod = method;
                    _log.Info("EXEC", $"Found lobby controller handler: {ReflectionHelper.FormatMethodSignature(method)}");
                    break;
                }
            }

            // Also check for two-parameter methods where first param is the settings type and second is bool
            if (handlerMethod == null)
            {
                foreach (var method in controllerMethods)
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length == 2 &&
                        parameters[0].ParameterType == settingsType &&
                        parameters[1].ParameterType == typeof(bool))
                    {
                        handlerMethod = method;
                        _log.Info("EXEC", $"Found lobby controller handler (2-param): {ReflectionHelper.FormatMethodSignature(method)}");
                        break;
                    }
                }
            }

            if (handlerMethod == null)
            {
                _log.Warn("EXEC", $"No method on lobby controller {controllerType.Name} accepts {settingsType.Name}. Cannot hook onSetGame.");
                return;
            }

            // Create a delegate for the handler and assign it to the popup's onSetGame field
            var handler = handlerMethod;
            var ctrl = controller;
            Delegate callbackDelegate;

            if (handler.GetParameters().Length == 1)
            {
                callbackDelegate = Delegate.CreateDelegate(delegateType, ctrl, handler);
            }
            else
            {
                // Two-param version: wrap in a lambda that passes (settings, true)
                // Use Action<T> that calls handler(settings, true)
                var invokeMethod = new Action<object>(settings =>
                {
                    handler.Invoke(ctrl, new[] { settings, (object)true });
                });

                // Build the correct Action<SettingsType> delegate
                var actionType = typeof(Action<>).MakeGenericType(settingsType);
                // We can't use Delegate.CreateDelegate with a lambda targeting object, so use DynamicInvoke wrapper
                callbackDelegate = Delegate.CreateDelegate(actionType, ctrl, handler.Name);
            }

            onSetGameField.SetValue(popupInstance, callbackDelegate);
            _log.Info("EXEC", $"Hooked onSetGame callback on popup to lobby controller via {handler.Name}");
        }
        catch (Exception ex)
        {
            _log.Warn("EXEC", $"Failed to hook onSetGame callback: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool TryBuildPopupContext(object popup, out PopupContext context)
    {
        context = default;
        var contentPanel = ReflectionHelper.GetMemberValue(popup, "panelContentSetupPanel");
        if (contentPanel == null)
            return false;

        var dropdown = ReflectionHelper.GetMemberValue(contentPanel, "dropdownContentSelection");
        if (dropdown == null)
            return false;

        var environmentDropdown = ReflectionHelper.GetMemberValue(contentPanel, "dropdownEnvironmentSelection");

        context = new PopupContext
        {
            Popup = popup,
            ContentPanel = contentPanel,
            Dropdown = dropdown,
            EnvironmentDropdown = environmentDropdown
        };

        return true;
    }

    private bool ConfigurePopupForRequest(PopupContext context, ChangeRequest request, out string selectionSummary)
    {
        selectionSummary = string.Empty;
        if (request.CycleNext)
            return TryCycleNextSelection(context, out selectionSummary);

        var summaries = new List<string>();

        if (!EnsureEnvironmentForRequest(context, request, summaries))
            return false;

        if (TrySelectConfiguredContent(context, request, out var contentSummary))
        {
            summaries.Add(contentSummary);
            selectionSummary = string.Join("; ", summaries.Where(summary => !string.IsNullOrWhiteSpace(summary)));
            return true;
        }

        if (HasContentTarget(request) &&
            string.IsNullOrWhiteSpace(request.EnvironmentName) &&
            TrySearchAcrossEnvironments(context, request, summaries, out selectionSummary))
        {
            return true;
        }

        if (!HasContentTarget(request) && summaries.Count > 0)
        {
            selectionSummary = string.Join("; ", summaries.Where(summary => !string.IsNullOrWhiteSpace(summary)));
            return true;
        }

        return false;
    }

    private bool TryCycleNextSelection(PopupContext context, out string selectionSummary)
    {
        selectionSummary = string.Empty;
        if (context.Dropdown is not Dropdown dropdown)
            return false;

        if (dropdown.options.Count <= 1)
            return false;

        var nextIndex = (dropdown.value + 1) % dropdown.options.Count;
        dropdown.value = nextIndex;
        selectionSummary = $"cycled dropdown to index={nextIndex} caption=\"{dropdown.options[nextIndex].text}\"";
        return true;
    }

    private bool EnsureEnvironmentForRequest(PopupContext context, ChangeRequest request, List<string> summaries)
    {
        if (string.IsNullOrWhiteSpace(request.EnvironmentName))
            return true;

        if (!TrySelectEnvironment(context, request.EnvironmentName, out var environmentSummary))
        {
            _log.Warn("EXEC", $"Requested environment/map \"{request.EnvironmentName}\" was not found in the environment selector.");
            DumpEnvironmentOptions(context);
            return false;
        }

        summaries.Add(environmentSummary);
        return true;
    }

    private bool TrySelectConfiguredContent(PopupContext context, ChangeRequest request, out string selectionSummary)
    {
        selectionSummary = string.Empty;

        if (TrySelectViaDropdownData(context, request, out selectionSummary))
            return true;

        if (!string.IsNullOrWhiteSpace(request.RaceName) && TrySelectRace(context, request, out selectionSummary))
            return true;

        if (!string.IsNullOrWhiteSpace(request.TrackName) && TrySelectTrack(context, request, out selectionSummary))
            return true;

        if (!string.IsNullOrWhiteSpace(request.WorkshopId))
        {
            if (TrySelectRace(context, request, out selectionSummary))
                return true;
            if (TrySelectTrack(context, request, out selectionSummary))
                return true;
        }

        return false;
    }

    private bool TrySearchAcrossEnvironments(PopupContext context, ChangeRequest request, List<string> prefixSummaries, out string selectionSummary)
    {
        if (_diag != null) _diag.TrySearchAcrossEnvironmentsCalls++;

        selectionSummary = string.Empty;
        if (context.EnvironmentDropdown is not Dropdown environmentDropdown)
            return false;

        var originalIndex = environmentDropdown.value;
        foreach (var candidate in GetDropdownCandidates(context.EnvironmentDropdown))
        {
            if (candidate.Index == originalIndex)
                continue;

            if (_diag != null) _diag.EnvironmentCandidatesIterated++;

            if (!ApplyEnvironmentCandidate(context, candidate, out var environmentSummary))
                continue;

            if (!TrySelectConfiguredContent(context, request, out var contentSummary))
                continue;

            var summaries = new List<string>(prefixSummaries)
            {
                environmentSummary,
                contentSummary
            };
            selectionSummary = string.Join("; ", summaries.Where(summary => !string.IsNullOrWhiteSpace(summary)));
            return true;
        }

        if (originalIndex >= 0 && originalIndex < environmentDropdown.options.Count)
        {
            _ = ApplyEnvironmentCandidate(context, new DropdownCandidate(originalIndex, environmentDropdown.options[originalIndex].text ?? string.Empty, null), out _);
        }

        return false;
    }

    private bool TrySelectEnvironment(PopupContext context, string targetEnvironmentName, out string selectionSummary)
    {
        selectionSummary = string.Empty;
        if (context.EnvironmentDropdown == null)
            return false;

        foreach (var candidate in GetDropdownCandidates(context.EnvironmentDropdown))
        {
            if (!MatchesEnvironmentCandidate(candidate, targetEnvironmentName))
                continue;

            return ApplyEnvironmentCandidate(context, candidate, out selectionSummary);
        }

        return false;
    }

    private bool TrySelectViaDropdownData(PopupContext context, ChangeRequest request, out string selectionSummary)
    {
        selectionSummary = string.Empty;

        foreach (var candidate in GetDropdownCandidates(context.Dropdown))
        {
            if (!MatchesDropdownCandidate(candidate, request))
                continue;

            if (!ApplyDropdownCandidate(context, candidate))
                continue;

            selectionSummary = $"dropdown index={candidate.Index} caption=\"{candidate.Caption}\" data={FormatOptionSummary(candidate.Data)}";
            return true;
        }

        return false;
    }

    private bool TryApplyLiveSessionSettings(PopupContext context)
    {
        var sessionSettings = GetLiveSessionSettings();
        if (sessionSettings == null)
        {
            _log.Warn("EXEC", "Could not resolve live session settings from CurrentContentContainer.");
            return false;
        }

        var applied = false;
        applied |= TryInvokeSingleArgument(context.Popup, "ApplyFromGameSettings", sessionSettings);
        applied |= TryInvokeSingleArgument(context.ContentPanel, "ApplyFromGameSettings", sessionSettings);

        return applied;
    }

    private bool TryInvokePopupSetGameCallback(PopupContext context)
    {
        var callback = ReflectionHelper.GetMemberValue(context.Popup, "onSetGame") as Delegate;
        if (callback == null &&
            MultiplayerRuntimeState.TryGetLatestPopupWithSetGameCallback(out _, out var cachedCallback, out var callbackSource, out var callbackCapturedUtc) &&
            cachedCallback != null)
        {
            callback = cachedCallback;
            _log.Info("EXEC", $"Using cached onSetGame delegate from {callbackSource} capturedAt={callbackCapturedUtc:O}");
        }

        if (callback == null)
        {
            _log.Warn("EXEC", "Popup is inactive and onSetGame callback was not available.");
            return false;
        }

        var payload = CreatePopupGameSettingsPayload(context.Popup, callback);
        if (payload == null)
            return false;

        var applyToGameSettings = ReflectionHelper.FindMethod(context.Popup.GetType(), "ApplyToGameSettings", 1);
        if (applyToGameSettings == null)
        {
            _log.Warn("EXEC", "PopupQuickPlayMultiplayerSetup.ApplyToGameSettings could not be found for direct callback invocation.");
            return false;
        }

        try
        {
            applyToGameSettings.Invoke(context.Popup, new[] { payload });
            _log.Info("EXEC", $"Prepared popup game settings payload via {ReflectionHelper.FormatMethodSignature(applyToGameSettings)}");
            callback.DynamicInvoke(payload);
            _log.Info("EXEC", $"Invoked popup onSetGame callback directly via {callback.GetType().FullName}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("EXEC", $"Direct popup onSetGame callback failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private object? CreatePopupGameSettingsPayload(object popup, Delegate callback)
    {
        var payloadType = callback.GetType().GenericTypeArguments.FirstOrDefault();
        if (payloadType == null)
        {
            var applyMethod = ReflectionHelper.FindMethod(popup.GetType(), "ApplyToGameSettings", 1);
            payloadType = applyMethod?.GetParameters().FirstOrDefault()?.ParameterType;
        }

        if (payloadType == null)
        {
            _log.Warn("EXEC", "Could not resolve popup game settings payload type.");
            return null;
        }

        try
        {
            return Activator.CreateInstance(payloadType);
        }
        catch (Exception ex)
        {
            _log.Warn("EXEC", $"Failed to create popup game settings payload of type {payloadType.FullName}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static bool TryGetCustomProperty(Hashtable table, string key, out object value)
    {
        value = null!;
        if (table == null || !table.ContainsKey(key))
            return false;

        value = table[key];
        return value != null;
    }

    private object? GetRoomGameModeSnapshot()
    {
        var room = PhotonNetwork.CurrentRoom;
        if (room?.CustomProperties == null)
            return null;

        if (!TryGetCustomProperty(room.CustomProperties, "GM", out var gameModeValue))
            return null;

        var gameModeType = _discovery.TryResolveKnownType("Liftoff.Multiplayer.GameMode");
        if (gameModeType == null)
            return null;

        try
        {
            var enumValue = Enum.ToObject(gameModeType, Convert.ToInt32(gameModeValue));
            _log.Info("EXEC", $"Captured room game mode snapshot GM={gameModeValue} -> {enumValue}");
            return enumValue;
        }
        catch (Exception ex)
        {
            _log.Warn("EXEC", $"Failed to capture room game mode snapshot: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private bool? GetRoomSlipstreamSnapshot()
    {
        var room = PhotonNetwork.CurrentRoom;
        if (room?.CustomProperties == null)
            return null;

        if (!TryGetCustomProperty(room.CustomProperties, "S", out var slipstreamValue))
            return null;

        try
        {
            var value = Convert.ToBoolean(slipstreamValue);
            _log.Info("EXEC", $"Captured room slipstream snapshot S={slipstreamValue}");
            return value;
        }
        catch (Exception ex)
        {
            _log.Warn("EXEC", $"Failed to capture room slipstream snapshot: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private bool TrySelectRace(PopupContext context, ChangeRequest request, out string selectionSummary)
    {
        selectionSummary = string.Empty;

        var gameModeType = _discovery.TryResolveKnownType("Liftoff.Multiplayer.GameMode");
        if (gameModeType != null)
        {
            var classicRace = Enum.Parse(gameModeType, "ClassicRace");
            ReflectionHelper.SetMemberValue(context.ContentPanel, "SelectedGameMode", classicRace);
        }

        foreach (var optionSet in GetSelectionOptionSets(context, "SelectedRace", "RaceQuickInfo", "GameContentEntry"))
        {
            var match = optionSet.Options.FirstOrDefault(option => MatchesContent(option, request, preferRaceName: true));
            if (match == null)
                continue;

            ReflectionHelper.SetMemberValue(context.ContentPanel, "SelectedRace", match);
            selectionSummary = $"race type={optionSet.OptionType.FullName} name=\"{ReadContentName(match)}\" localId=\"{ReadContentIdentifier(match, "LocalID")}\" managedId=\"{ReadContentIdentifier(match, "ManagedID")}\"";
            return true;
        }

        return false;
    }

    private bool TrySelectTrack(PopupContext context, ChangeRequest request, out string selectionSummary)
    {
        selectionSummary = string.Empty;

        foreach (var optionSet in GetSelectionOptionSets(context, "SelectedTrack", "TrackQuickInfo", "GameContentEntry"))
        {
            var match = optionSet.Options.FirstOrDefault(option => MatchesContent(option, request, preferRaceName: false));
            if (match == null)
                continue;

            ReflectionHelper.SetMemberValue(context.ContentPanel, "SelectedTrack", match);
            selectionSummary = $"track type={optionSet.OptionType.FullName} name=\"{ReadContentName(match)}\" localId=\"{ReadContentIdentifier(match, "LocalID")}\" managedId=\"{ReadContentIdentifier(match, "ManagedID")}\"";
            return true;
        }

        return false;
    }

    private List<object> GetDropdownDataOptions(object dropdown, Type contentType)
    {
        if (_diag != null) _diag.GetDropdownDataOptionsCalls++;

        var method = AccessTools.Method(dropdown.GetType(), "GetOptionDataAs");
        if (method == null)
            return new List<object>();

        try
        {
            if (_diag != null) _diag.MakeGenericMethodCalls++;
            var genericMethod = method.MakeGenericMethod(contentType);
            var result = genericMethod.Invoke(dropdown, Array.Empty<object>());
            var list = ReflectionHelper.EnumerateAsObjects(result).ToList();
            if (_diag != null) _diag.GetDropdownDataOptionsTotalItems += list.Count;
            return list;
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException;
            _log.Warn("EXEC", $"GetOptionDataAs<{contentType.FullName}> failed: {ex.GetType().Name}: {ex.Message}" +
                (inner != null ? $" → {inner.GetType().Name}: {inner.Message}" : ""));
            return new List<object>();
        }
    }

    private bool MatchesContent(object option, ChangeRequest request, bool preferRaceName)
    {
        var name = ReadContentName(option);
        var localId = ReadContentIdentifier(option, "LocalID");
        var managedId = ReadContentIdentifier(option, "ManagedID");
        var trackDependency = ReadTrackDependency(option);

        if (preferRaceName && MatchesName(name, request.RaceName))
            return true;
        if (!preferRaceName && MatchesName(name, request.TrackName))
            return true;

        if (!string.IsNullOrWhiteSpace(request.WorkshopId))
        {
            var workshopId = request.WorkshopId;
            if (ContainsOrdinalIgnoreCase(localId, workshopId) ||
                ContainsOrdinalIgnoreCase(managedId, workshopId) ||
                ContainsOrdinalIgnoreCase(trackDependency, workshopId))
            {
                return true;
            }
        }

        return false;
    }

    private void LogOptionSnapshot(PopupContext context)
    {
        try
        {
            // Current selection state BEFORE configure runs
            var selectedEnv = GetContentPanelMemberValue(context, "SelectedEnvironment");
            var selectedEnvName = GetContentPanelMemberValue(context, "SelectedEnvironmentName");
            _log.Info("DIAG", $"Pre-configure SelectedEnvironment={FormatEnvironmentSummary(selectedEnv)} SelectedEnvironmentName={selectedEnvName}");
            _log.Info("DIAG", $"Pre-configure SelectedTrack={FormatOptionSummary(GetContentPanelMemberValue(context, "SelectedTrack"))} SelectedRace={FormatOptionSummary(GetContentPanelMemberValue(context, "SelectedRace"))}");

            // Environment dropdown snapshot with selected index
            if (context.EnvironmentDropdown is Dropdown envDd)
            {
                var selectedCaption = envDd.value >= 0 && envDd.value < envDd.options.Count
                    ? envDd.options[envDd.value].text : "?";
                var envNames = new List<string>();
                for (var i = 0; i < Math.Min(envDd.options.Count, 5); i++)
                    envNames.Add(envDd.options[i].text ?? "?");
                var suffix = envDd.options.Count > 5 ? "..." : "";
                _log.Info("DIAG", $"Environments: count={envDd.options.Count} selectedIdx={envDd.value} selectedCaption=\"{selectedCaption}\" [{string.Join(", ", envNames)}{suffix}]");
            }
            else
            {
                _log.Warn("DIAG", "EnvironmentDropdown is null or not a Dropdown");
            }

            // Track/content dropdown snapshot with selected index
            if (context.Dropdown is Dropdown contentDd)
            {
                var selectedCaption = contentDd.value >= 0 && contentDd.value < contentDd.options.Count
                    ? contentDd.options[contentDd.value].text : "?";
                var trackNames = new List<string>();
                for (var i = 0; i < Math.Min(contentDd.options.Count, 5); i++)
                    trackNames.Add(contentDd.options[i].text ?? "?");
                var suffix = contentDd.options.Count > 5 ? "..." : "";
                _log.Info("DIAG", $"Content: count={contentDd.options.Count} selectedIdx={contentDd.value} selectedCaption=\"{selectedCaption}\" [{string.Join(", ", trackNames)}{suffix}]");
            }

            // Race option sets
            foreach (var optionSet in GetSelectionOptionSets(context, "SelectedRace", "RaceQuickInfo", "GameContentEntry"))
            {
                var raceNames = new List<string>();
                for (var i = 0; i < Math.Min(optionSet.Options.Count, 5); i++)
                    raceNames.Add(ReadContentName(optionSet.Options[i]) ?? "?");
                var suffix = optionSet.Options.Count > 5 ? "..." : "";
                _log.Info("DIAG", $"Races ({optionSet.OptionType.Name}): count={optionSet.Options.Count} [{string.Join(", ", raceNames)}{suffix}]");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("DIAG", $"Option snapshot failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DumpPopupOptions(PopupContext context)
    {
        if (context.Dropdown is not Dropdown dropdown)
            return;

        _log.Info("EXEC", $"Popup option count={dropdown.options.Count}");
        for (var index = 0; index < dropdown.options.Count; index++)
        {
            var option = dropdown.options[index];
            _log.Info("EXEC", $"  option[{index}] caption=\"{option.text}\"");
        }

        _log.Info("EXEC", $"Current SelectedEnvironment={FormatEnvironmentSummary(GetContentPanelMemberValue(context, "SelectedEnvironment"))}");
        _log.Info("EXEC", $"Current SelectedTrack={FormatOptionSummary(GetContentPanelMemberValue(context, "SelectedTrack"))}");
        _log.Info("EXEC", $"Current SelectedRace={FormatOptionSummary(GetContentPanelMemberValue(context, "SelectedRace"))}");
        _log.Info("EXEC", $"Current SelectedGameMode={ReflectionHelper.SafeDescribe(_describe, GetContentPanelMemberValue(context, "SelectedGameMode"))}");
        DumpSelectedContent(context);
        DumpEnvironmentOptions(context);

        foreach (var candidate in GetDropdownCandidates(context.Dropdown).Take(16))
            _log.Info("EXEC", $"dropdown[{candidate.Index}] caption=\"{candidate.Caption}\" data={FormatOptionSummary(candidate.Data)}");

        foreach (var optionSet in GetSelectionOptionSets(context, "SelectedTrack", "TrackQuickInfo", "GameContentEntry"))
            DumpOptionSet("track", optionSet);

        foreach (var optionSet in GetSelectionOptionSets(context, "SelectedRace", "RaceQuickInfo", "GameContentEntry"))
            DumpOptionSet("race", optionSet);
    }

    private void DumpOptionSet(string label, SelectionOptionSet optionSet)
    {
        _log.Info("EXEC", $"{label} option type={optionSet.OptionType.FullName} count={optionSet.Options.Count}");
        for (var index = 0; index < Math.Min(optionSet.Options.Count, 12); index++)
            _log.Info("EXEC", $"  {label}[{index}] {FormatOptionSummary(optionSet.Options[index])}");
    }

    private IEnumerable<SelectionOptionSet> GetSelectionOptionSets(PopupContext context, string memberName, params string[] fallbackTypeNames)
    {
        if (_diag != null) _diag.GetSelectionOptionSetsCalls++;

        var seen = new HashSet<Type>();
        foreach (var optionType in ResolveSelectionTypes(context, memberName, fallbackTypeNames))
        {
            if (!seen.Add(optionType))
                continue;

            var options = GetDropdownDataOptions(context.Dropdown, optionType);
            if (options.Count == 0)
                continue;

            yield return new SelectionOptionSet(optionType, options);
        }
    }

    private IEnumerable<DropdownCandidate> GetDropdownCandidates(object? dropdownObject)
    {
        if (_diag != null) _diag.GetDropdownCandidatesCalls++;

        if (dropdownObject is not Dropdown dropdown)
            yield break;

        if (_diag != null) _diag.GetDropdownCandidatesTotalItems += dropdown.options.Count;

        for (var index = 0; index < dropdown.options.Count; index++)
        {
            var option = dropdown.options[index];
            var data = ReflectionHelper.GetMemberValue(option, "Data") ??
                       ReflectionHelper.GetMemberValue(option, "data");

            yield return new DropdownCandidate(index, option.text ?? string.Empty, data);
        }
    }

    private bool MatchesDropdownCandidate(DropdownCandidate candidate, ChangeRequest request)
    {
        if (candidate.Data != null)
        {
            if (MatchesContent(candidate.Data, request, preferRaceName: true) ||
                MatchesContent(candidate.Data, request, preferRaceName: false))
            {
                return true;
            }
        }

        if (MatchesName(candidate.Caption, request.RaceName) || MatchesName(candidate.Caption, request.TrackName))
            return true;

        return false;
    }

    private bool MatchesEnvironmentCandidate(DropdownCandidate candidate, string targetEnvironmentName)
    {
        if (string.IsNullOrWhiteSpace(targetEnvironmentName))
            return false;

        if (MatchesName(candidate.Caption, targetEnvironmentName))
            return true;

        if (candidate.Data == null)
            return false;

        return MatchesName(ReadEnvironmentDisplayName(candidate.Data), targetEnvironmentName) ||
               MatchesName(ReadEnvironmentInternalName(candidate.Data), targetEnvironmentName);
    }

    private bool ApplyDropdownCandidate(PopupContext context, DropdownCandidate candidate)
    {
        var applied = false;

        if (candidate.Data != null)
        {
            applied |= TrySetDropdownSelectionByData(context.Dropdown, candidate.Data);
        }

        if (context.Dropdown is Dropdown dropdown)
        {
            try
            {
                if (dropdown.value != candidate.Index)
                    dropdown.value = candidate.Index;

                dropdown.RefreshShownValue();
                applied = true;
            }
            catch (Exception ex)
            {
                _log.Warn("EXEC", $"Failed to move dropdown to index {candidate.Index}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        TryNotifyContentSelected(context, candidate.Index);
        return applied;
    }

    private bool ApplyEnvironmentCandidate(PopupContext context, DropdownCandidate candidate, out string selectionSummary)
    {
        selectionSummary = string.Empty;
        if (context.EnvironmentDropdown == null)
            return false;

        var applied = false;
        if (candidate.Data != null)
        {
            TrySetSelectedEnvironment(context, candidate.Data);
            applied |= TrySetDropdownSelectionByData(context.EnvironmentDropdown, candidate.Data);
        }

        if (context.EnvironmentDropdown is Dropdown dropdown)
        {
            try
            {
                if (dropdown.value != candidate.Index)
                    dropdown.value = candidate.Index;

                dropdown.RefreshShownValue();
                applied = true;
            }
            catch (Exception ex)
            {
                _log.Warn("EXEC", $"Failed to move environment dropdown to index {candidate.Index}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (!TryInvokeSelectionMethod(context.ContentPanel, "OnEnvironmentSelected", candidate.Index))
            InvokeZeroArg(context.ContentPanel, "FillEnvironmentSelection");

        InvokeZeroArg(context.ContentPanel, "FillContentSelection");
        InvokeZeroArg(context.ContentPanel, "InvokeCurrentValues");

        selectionSummary = $"environment index={candidate.Index} caption=\"{candidate.Caption}\" data={FormatEnvironmentSummary(candidate.Data)}";
        return applied;
    }

    private void TrySetSelectedEnvironment(PopupContext context, object environmentData)
    {
        try
        {
            ReflectionHelper.SetMemberValue(context.ContentPanel, "SelectedEnvironment", environmentData);
        }
        catch (Exception ex)
        {
            _log.Warn("EXEC", $"Failed to apply SelectedEnvironment directly: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool TrySetDropdownSelectionByData(object dropdown, object data)
    {
        var methods = dropdown.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == "SetSelectedOptionForData")
            .ToList();

        foreach (var method in methods)
        {
            try
            {
                if (method.IsGenericMethodDefinition)
                {
                    var generic = method.MakeGenericMethod(data.GetType());
                    generic.Invoke(dropdown, new[] { data });
                    _log.Info("EXEC", $"Selected dropdown data via {ReflectionHelper.FormatMethodSignature(generic)}");
                    return true;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 1)
                    continue;

                if (!parameters[0].ParameterType.IsInstanceOfType(data) && parameters[0].ParameterType != typeof(object))
                    continue;

                method.Invoke(dropdown, new[] { data });
                _log.Info("EXEC", $"Selected dropdown data via {ReflectionHelper.FormatMethodSignature(method)}");
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn("EXEC", $"SetSelectedOptionForData on {dropdown.GetType().FullName} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return false;
    }

    private void TryNotifyContentSelected(PopupContext context, int index)
    {
        if (TryInvokeSelectionMethod(context.ContentPanel, "OnContentSelected", index))
            return;

        InvokeZeroArg(context.ContentPanel, "InvokeCurrentValues");
    }

    private void DumpSelectedContent(PopupContext context)
    {
        var selectedContent = GetSelectedContentItems(context).ToList();
        _log.Info("EXEC", $"selectedContent count={selectedContent.Count}");
        for (var index = 0; index < Math.Min(selectedContent.Count, 8); index++)
            _log.Info("EXEC", $"  selectedContent[{index}] {FormatOptionSummary(selectedContent[index])}");
    }

    private void DumpEnvironmentOptions(PopupContext context)
    {
        if (context.EnvironmentDropdown is not Dropdown dropdown)
            return;

        _log.Info("EXEC", $"Environment option count={dropdown.options.Count}");
        foreach (var candidate in GetDropdownCandidates(context.EnvironmentDropdown).Take(12))
            _log.Info("EXEC", $"environment[{candidate.Index}] caption=\"{candidate.Caption}\" data={FormatEnvironmentSummary(candidate.Data)}");
    }

    private object? GetLiveSessionSettings()
    {
        if (MultiplayerRuntimeState.TryGetLatestGameSettingsSnapshot(out var cachedSettings, out var cachedSource, out var capturedUtc))
        {
            _log.Info("EXEC", $"Using cached game settings snapshot from {cachedSource} capturedAt={capturedUtc:O}");
            return cachedSettings;
        }

        var containerType = AccessTools.TypeByName("CurrentContentContainer");
        if (containerType == null)
            return null;

        foreach (var container in ReflectionHelper.GetLiveObjects(containerType))
        {
            var sessionSettings = ReflectionHelper.GetMemberValue(container, "SessionSettings") ??
                                  ReflectionHelper.GetMemberValue(container, "sessionSettings");
            if (sessionSettings != null)
                return sessionSettings;
        }

        return null;
    }

    private bool HasSelectableContent(PopupContext context)
    {
        return GetPopupOptionCount(context) > 0 || GetSelectedContentCount(context) > 0;
    }

    private int GetPopupOptionCount(PopupContext context)
    {
        return context.Dropdown is Dropdown dropdown ? dropdown.options.Count : 0;
    }

    private int GetSelectedContentCount(PopupContext context)
    {
        return GetSelectedContentItems(context).Count();
    }

    private void InvokeZeroArg(object instance, string methodName)
    {
        try
        {
            var method = ReflectionHelper.FindMethod(instance.GetType(), methodName, 0);
            if (method == null)
                return;

            method.Invoke(instance, Array.Empty<object>());
            _log.Info("EXEC", $"Invoked {ReflectionHelper.FormatMethodSignature(method)}");
        }
        catch (Exception ex)
        {
            _log.Warn("EXEC", $"{methodName} invocation failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool TryInvokeSingleArgument(object instance, string methodName, object argument)
    {
        var methods = instance.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == methodName && method.GetParameters().Length == 1)
            .ToList();

        foreach (var method in methods)
        {
            try
            {
                var parameterType = method.GetParameters()[0].ParameterType;
                if (!parameterType.IsInstanceOfType(argument) && !parameterType.IsAssignableFrom(argument.GetType()))
                    continue;

                method.Invoke(instance, new[] { argument });
                _log.Info("EXEC", $"Invoked {ReflectionHelper.FormatMethodSignature(method)} with {argument.GetType().FullName}");
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn("EXEC", $"{methodName} invocation failed on {instance.GetType().FullName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return false;
    }

    private bool TryInvokeSelectionMethod(object instance, string methodName, object argument)
    {
        var methods = instance.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == methodName && method.GetParameters().Length == 1)
            .ToList();

        foreach (var method in methods)
        {
            try
            {
                var parameterType = method.GetParameters()[0].ParameterType;
                if (parameterType == typeof(int) && argument is int integer)
                {
                    method.Invoke(instance, new object[] { integer });
                    _log.Info("EXEC", $"Invoked {ReflectionHelper.FormatMethodSignature(method)} with index={integer}");
                    return true;
                }

                if (parameterType.IsInstanceOfType(argument) || parameterType.IsAssignableFrom(argument.GetType()))
                {
                    method.Invoke(instance, new[] { argument });
                    _log.Info("EXEC", $"Invoked {ReflectionHelper.FormatMethodSignature(method)} with {argument.GetType().FullName}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log.Warn("EXEC", $"{methodName} invocation failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return false;
    }

    private IEnumerable<Type> ResolveSelectionTypes(PopupContext context, string memberName, params string[] fallbackTypeNames)
    {
        var declaredType = ReflectionHelper.GetMemberType(context.ContentPanel, memberName);
        if (declaredType != null && declaredType != typeof(object))
            yield return declaredType;

        foreach (var candidateType in GetDropdownCandidates(context.Dropdown)
                     .Select(candidate => candidate.Data?.GetType())
                     .Where(type => type != null)
                     .Cast<Type>())
        {
            yield return candidateType;
        }

        foreach (var selectedType in GetSelectedContentItems(context).Select(item => item.GetType()))
            yield return selectedType;

        var currentValue = GetContentPanelMemberValue(context, memberName);
        if (currentValue != null)
            yield return currentValue.GetType();

        foreach (var typeName in fallbackTypeNames)
        {
            var resolved = _discovery.TryResolveKnownType(typeName);
            if (resolved != null)
                yield return resolved;
        }
    }

    private IEnumerable<object> GetSelectedContentItems(PopupContext context)
    {
        return ReflectionHelper.EnumerateAsObjects(GetContentPanelMemberValue(context, "selectedContent"));
    }

    private object? GetContentPanelMemberValue(PopupContext context, string memberName)
    {
        if (ReflectionHelper.TryGetMemberValue(context.ContentPanel, memberName, out var value, out var exception))
            return value;

        if (exception != null)
        {
            var inner = exception.InnerException;
            _log.Warn("EXEC", $"Reading {context.ContentPanel.GetType().FullName}.{memberName} failed: {exception.GetType().Name}: {exception.Message}" +
                (inner != null ? $" → {inner.GetType().Name}: {inner.Message}" : ""));
        }

        return null;
    }

    private static string FormatOptionSummary(object? option)
    {
        if (option == null)
            return "<null>";

        var builder = new StringBuilder();
        builder.Append(option.GetType().FullName);
        builder.Append(" name=\"").Append(ReadContentName(option)).Append('"');

        var localId = ReadContentIdentifier(option, "LocalID");
        if (!string.IsNullOrWhiteSpace(localId))
            builder.Append(" localId=\"").Append(localId).Append('"');

        var managedId = ReadContentIdentifier(option, "ManagedID");
        if (!string.IsNullOrWhiteSpace(managedId))
            builder.Append(" managedId=\"").Append(managedId).Append('"');

        var trackDependency = ReadTrackDependency(option);
        if (!string.IsNullOrWhiteSpace(trackDependency))
            builder.Append(" trackDependency=\"").Append(trackDependency).Append('"');

        return builder.ToString();
    }

    private static string FormatEnvironmentSummary(object? environment)
    {
        if (environment == null)
            return "<null>";

        var displayName = ReadEnvironmentDisplayName(environment);
        var internalName = ReadEnvironmentInternalName(environment);
        if (string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(internalName))
            return environment.ToString() ?? environment.GetType().FullName ?? "<unknown-environment>";

        var builder = new StringBuilder();
        builder.Append(environment.GetType().FullName);
        if (!string.IsNullOrWhiteSpace(displayName))
            builder.Append(" displayName=\"").Append(displayName).Append('"');
        if (!string.IsNullOrWhiteSpace(internalName))
            builder.Append(" internalName=\"").Append(internalName).Append('"');
        return builder.ToString();
    }

    private void DumpKnownLiveObjects(string category, string typeName)
    {
        var type = _discovery.TryResolveKnownType(typeName);
        foreach (var liveObject in ReflectionHelper.GetLiveObjects(type).Take(3))
        {
            _log.Info(category, $"live object {ReflectionHelper.DescribeObjectIdentity(liveObject)}");
            _log.Info(category, $"snapshot {ReflectionHelper.SafeDescribe(_describe, liveObject)}");
        }
    }

    private static int GetInstanceId(object value)
    {
        return value is UnityEngine.Object unityObject ? unityObject.GetInstanceID() : value.GetHashCode();
    }

    private static bool IsPopupActive(object popup)
    {
        if (popup == null)
            return false;

        if (popup is UnityEngine.Object unityObject && unityObject == null)
            return false;

        if (popup is Behaviour behaviour)
        {
            try
            {
                return behaviour.isActiveAndEnabled || (behaviour.gameObject != null && behaviour.gameObject.activeInHierarchy);
            }
            catch
            {
                return false;
            }
        }

        if (!ReflectionHelper.TryGetMemberValue(popup, "gameObject", out var gameObjectValue, out _))
            gameObjectValue = null;

        var gameObject = gameObjectValue as GameObject;
        if (gameObject != null)
        {
            try
            {
                return gameObject.activeInHierarchy;
            }
            catch
            {
                return false;
            }
        }

        if (!ReflectionHelper.TryGetMemberValue(popup, "isActiveAndEnabled", out var isActive, out _))
            return false;

        if (isActive is bool activeValue)
            return activeValue;

        return false;
    }

    private static bool IsPopupSceneLoaded(object popup)
    {
        if (popup == null)
            return false;

        if (popup is UnityEngine.Object unityObject && unityObject == null)
            return false;

        try
        {
            GameObject? go = null;
            if (popup is Component component)
                go = component.gameObject;
            else if (ReflectionHelper.TryGetMemberValue(popup, "gameObject", out var goValue, out _))
                go = goValue as GameObject;

            if (go == null)
                return false;

            var scene = go.scene;
            return scene.IsValid() && scene.isLoaded;
        }
        catch
        {
            return false;
        }
    }

    // After a Photon disconnect the popup's GameObject can survive scene reloads
    // with surface checks (active+sceneLoaded+callback) still passing while its
    // ContentSettingsPanel internals (SelectedRace getter, dropdown data sources)
    // are wired to destroyed objects. Calling OnSetGame on that state throws
    // NullReferenceException inside the game's confirm flow and wedges recovery.
    // This probe converts a 30s silent Step-5 timeout into an immediate Step-4
    // Failed by detecting the zombie state up front.
    //
    // NOTE: SelectedTrack is intentionally NOT checked here. A Liftoff game update
    // changed its internal type, causing it to throw InvalidCastException even on
    // fully healthy fresh popups during normal operation. SelectedRace is sufficient
    // to distinguish a zombie popup (destroyed internals) from a live one.
    private bool IsPopupHealthy(PopupContext context, out string failureReason)
    {
        failureReason = string.Empty;

        if (context.ContentPanel == null)
        {
            failureReason = "ContentPanel is null";
            return false;
        }

        if (!ReflectionHelper.TryGetMemberValue(context.ContentPanel, "SelectedRace", out _, out var raceEx) && raceEx != null)
        {
            failureReason = $"SelectedRace getter threw {raceEx.GetType().Name}: {raceEx.Message}";
            return false;
        }

        return true;
    }

    private static bool HasOnSetGameCallback(object popup)
    {
        if (popup == null)
            return false;

        if (popup is UnityEngine.Object unityObject && unityObject == null)
            return false;

        return ReflectionHelper.TryGetMemberValue(popup, "onSetGame", out var callback, out _) && callback is Delegate;
    }

    private static string ReadContentName(object option)
    {
        return ReflectionHelper.GetMemberValue(option, "Name") as string
               ?? ReflectionHelper.GetMemberValue(option, "name") as string
               ?? option.ToString()
               ?? string.Empty;
    }

    private static string ReadContentIdentifier(object option, string memberName)
    {
        if (ReflectionHelper.TryGetNestedString(option, out var text, memberName, "str"))
            return text;

        return string.Empty;
    }

    private static string ReadTrackDependency(object option)
    {
        if (ReflectionHelper.TryGetNestedString(option, out var text, "TrackDependency", "str"))
            return text;

        return string.Empty;
    }

    private static string ReadEnvironmentDisplayName(object environment)
    {
        return ReflectionHelper.GetMemberValue(environment, "DisplayName") as string
               ?? ReflectionHelper.GetMemberValue(environment, "displayName") as string
               ?? ReflectionHelper.GetMemberValue(environment, "environmentDisplayName") as string
               ?? string.Empty;
    }

    private static string ReadEnvironmentInternalName(object environment)
    {
        return ReflectionHelper.GetMemberValue(environment, "name") as string
               ?? ReflectionHelper.GetMemberValue(environment, "Name") as string
               ?? environment.ToString()
               ?? string.Empty;
    }

    private static bool ContainsOrdinalIgnoreCase(string source, string match)
    {
        return !string.IsNullOrWhiteSpace(source) &&
               source.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool MatchesName(string source, string target)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            return false;

        return string.Equals(source, target, StringComparison.OrdinalIgnoreCase) ||
               source.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool HasContentTarget(ChangeRequest request)
    {
        return !string.IsNullOrWhiteSpace(request.TrackName) ||
               !string.IsNullOrWhiteSpace(request.RaceName) ||
               !string.IsNullOrWhiteSpace(request.WorkshopId);
    }

    // ── Catalog snapshot ─────────────────────────────────────────────────────

    /// <summary>
    /// Reads the current multiplayer setup popup and returns a structured catalog
    /// of all available environments, tracks, and races.
    /// Returns false if the popup is not currently open/available.
    /// </summary>
    public bool TryCatalogSnapshot(out Dictionary<string, object?> catalog)
    {
        catalog = new Dictionary<string, object?>();

        if (!TryAcquirePopup(out var context, out var acquireStatus) || acquireStatus != PopupAcquireStatus.Acquired)
        {
            _log.Info("CATALOG", $"TryCatalogSnapshot: popup not acquired (status={acquireStatus})");
            return false;
        }

        // Save original environment index so we can restore it after scanning
        var originalEnvIndex = -1;
        if (context.EnvironmentDropdown is Dropdown envDropdown)
            originalEnvIndex = envDropdown.value;

        var envCandidates = GetDropdownCandidates(context.EnvironmentDropdown).ToList();
        var environments  = new List<Dictionary<string, object?>>();

        foreach (var envCandidate in envCandidates)
        {
            // Switch the popup to this environment so tracks/races update
            ApplyEnvironmentCandidate(context, envCandidate, out _);

            var entry = new Dictionary<string, object?> { ["caption"] = envCandidate.Caption };
            if (envCandidate.Data != null)
            {
                var displayName  = ReadEnvironmentDisplayName(envCandidate.Data);
                var internalName = ReadEnvironmentInternalName(envCandidate.Data);
                if (!string.IsNullOrWhiteSpace(displayName))  entry["display_name"]  = displayName;
                if (!string.IsNullOrWhiteSpace(internalName)) entry["internal_name"] = internalName;
            }

            // Tracks for this environment
            var tracks = new List<Dictionary<string, object?>>();
            foreach (var optionSet in GetSelectionOptionSets(context, "SelectedTrack", "TrackQuickInfo", "GameContentEntry"))
            {
                foreach (var option in optionSet.Options)
                    tracks.Add(new Dictionary<string, object?>
                    {
                        ["name"]             = ReadContentName(option),
                        ["local_id"]         = ReadContentIdentifier(option, "LocalID"),
                        ["track_dependency"] = ReadTrackDependency(option),
                    });
                break;
            }
            entry["tracks"] = tracks;

            entry["races"] = null; // populated once below, not per-environment

            environments.Add(entry);
            _log.Info("CATALOG", $"  env \"{envCandidate.Caption}\": tracks={tracks.Count}");
        }

        // Restore the original environment selection
        if (originalEnvIndex >= 0 && originalEnvIndex < envCandidates.Count)
            ApplyEnvironmentCandidate(context, envCandidates[originalEnvIndex], out _);

        var totalTracks = 0;
        foreach (var e in environments)
            totalTracks += (e["tracks"] as List<Dictionary<string, object?>>)?.Count ?? 0;

        // Game modes — enumerate the GameMode enum rather than duplicating track data
        var gameModes = new List<Dictionary<string, object?>>();
        var gameModeType = _discovery.TryResolveKnownType("Liftoff.Multiplayer.GameMode");
        if (gameModeType != null)
        {
            foreach (var name in Enum.GetNames(gameModeType))
                gameModes.Add(new Dictionary<string, object?> { ["name"] = name });
        }

        catalog["environments"] = environments;
        catalog["game_modes"]   = gameModes;
        _log.Info("CATALOG", $"TryCatalogSnapshot: full scan complete — environments={environments.Count} total_tracks={totalTracks} game_modes={gameModes.Count}");
        return true;
    }

    private sealed class ChangeRequest
    {
        public string EnvironmentName { get; set; } = string.Empty;
        public string TrackName { get; set; } = string.Empty;
        public string RaceName { get; set; } = string.Empty;
        public string WorkshopId { get; set; } = string.Empty;
        public bool CycleNext { get; set; }
    }

    private sealed class SelectionOptionSet
    {
        public SelectionOptionSet(Type optionType, List<object> options)
        {
            OptionType = optionType;
            Options = options;
        }

        public Type OptionType { get; }
        public List<object> Options { get; }
    }

    private sealed class DropdownCandidate
    {
        public DropdownCandidate(int index, string caption, object? data)
        {
            Index = index;
            Caption = caption;
            Data = data;
        }

        public int Index { get; }
        public string Caption { get; }
        public object? Data { get; }
    }

    private struct PopupContext
    {
        public object Popup;
        public object ContentPanel;
        public object Dropdown;
        public object? EnvironmentDropdown;
        public object? Controller;
        public string? ControllerMethod;
        public object? LobbyController;
    }

    private sealed class ControllerCandidate
    {
        public ControllerCandidate(string typeName, string openMethodName)
        {
            TypeName = typeName;
            OpenMethodName = openMethodName;
        }

        public string TypeName { get; }
        public string OpenMethodName { get; }
        public object Controller { get; private set; } = null!;

        public ControllerCandidate WithController(object controller)
        {
            return new ControllerCandidate(TypeName, OpenMethodName)
            {
                Controller = controller
            };
        }
    }
}
