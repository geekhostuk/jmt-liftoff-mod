using System;
using System.Diagnostics;
using System.Linq;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;

namespace JmtLiftoffMod.Features.MultiplayerTrackControl;

internal sealed class MultiplayerHostStateDetector
{
    private readonly MultiplayerDiscoveryService _discovery;
    private readonly MultiplayerTrackControlLog _log;
    private readonly Func<object?, string> _describe;

    // ── CurrentContentContainer lookup ────────────────────────────────────
    //
    // Capture() runs on the Unity main thread from the once-per-second poll AND
    // from every Photon room/player property callback (which bypass the poll
    // interval entirely), so in an 8-pilot race it can run many times a second.
    // Finding the container costs a Resources.FindObjectsOfTypeAll -- a walk of
    // every loaded UnityEngine object, loaded assets included -- plus a Cast +
    // ToList allocation of the whole result. Paying that per call is felt as a
    // general loss of smoothness rather than a single visible hitch.
    //
    // The container is effectively a singleton that survives a track change, so
    // it is cached and revalidated through Unity's destroyed-object null: if the
    // scene tears it down, the next Capture() finds the replacement.
    private object? _cachedContainer;
    private int _containerScans;
    private long _containerScanTotalMs;
    private DateTime _nextScanReportUtc = DateTime.MinValue;

    public MultiplayerHostStateDetector(
        MultiplayerDiscoveryService discovery,
        MultiplayerTrackControlLog log,
        Func<object?, string> describe)
    {
        _discovery = discovery;
        _log = log;
        _describe = describe;
    }

    public HostStateSnapshot Capture()
    {
        _discovery.Refresh();

        var snapshot = new HostStateSnapshot();
        var currentContentContainer = ResolveCurrentContentContainer();

        snapshot.CurrentContentContainer = currentContentContainer;
        snapshot.SessionSettings = ReflectionHelper.GetMemberValue(currentContentContainer, "SessionSettings");
        snapshot.Level = ReflectionHelper.GetMemberValue(currentContentContainer, "Level");
        snapshot.Track = ReflectionHelper.GetMemberValue(currentContentContainer, "Track");
        snapshot.Race = ReflectionHelper.GetMemberValue(currentContentContainer, "Race");

        if (currentContentContainer != null && ReflectionHelper.IsTruthy(ReflectionHelper.GetMemberValue(currentContentContainer, "InMultiplayer")))
        {
            snapshot.IsInMultiplayer = true;
            snapshot.InMultiplayerReason = "CurrentContentContainer.InMultiplayer";
        }
        else if (PhotonNetwork.InRoom)
        {
            snapshot.IsInMultiplayer = true;
            snapshot.InMultiplayerReason = "PhotonNetwork.InRoom";
        }
        else
        {
            snapshot.IsInMultiplayer = false;
            snapshot.InMultiplayerReason = "No multiplayer room detected";
        }

        snapshot.CurrentRoom = PhotonNetwork.CurrentRoom;
        snapshot.LocalPlayer = PhotonNetwork.LocalPlayer;

        if (PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient)
        {
            snapshot.IsHost = true;
            snapshot.HostReason = "PhotonNetwork.IsMasterClient";
        }
        else if (PhotonNetwork.CurrentRoom != null && PhotonNetwork.LocalPlayer != null)
        {
            snapshot.IsHost = PhotonNetwork.CurrentRoom.MasterClientId == PhotonNetwork.LocalPlayer.ActorNumber;
            snapshot.HostReason = $"Photon room MasterClientId={PhotonNetwork.CurrentRoom.MasterClientId} LocalActor={PhotonNetwork.LocalPlayer.ActorNumber}";
        }
        else
        {
            snapshot.IsHost = false;
            snapshot.HostReason = "Photon host state unavailable";
        }

        snapshot.RoomProperties = PhotonNetwork.CurrentRoom?.CustomProperties;
        snapshot.IsInLobbyWaitingRoom = snapshot.IsInMultiplayer && snapshot.Level == null;

        // Screen detection: check if the multiplayer lobby panel (with buttonCreateRoom) is live
        // and ACTIVE — Resources.FindObjectsOfTypeAll returns stale objects after disconnect,
        // so Count > 0 alone gives a false positive. Require isActiveAndEnabled.
        snapshot.IsPhotonConnected = PhotonNetwork.IsConnectedAndReady;
        snapshot.PhotonClientState = PhotonNetwork.NetworkClientState.ToString();

        // The lobby-screen probe runs FindTypesByFieldName + Resources.FindObjectsOfTypeAll,
        // which are relatively expensive. They are only meaningful when we are NOT already in
        // a room (the lobby/create-room panel cannot be the active screen mid-race). Skipping
        // this while InRoom removes the per-poll reflection cost that caused in-game stutter.
        if (snapshot.IsInMultiplayer)
        {
            snapshot.IsOnMultiplayerLobbyScreen = false;
        }
        else
        {
            var lobbyPanelTypes = _discovery.FindTypesByFieldName("buttonCreateRoom");
            snapshot.IsOnMultiplayerLobbyScreen = lobbyPanelTypes.Any(type =>
                ReflectionHelper.GetLiveObjects(type).Any(obj => obj is UnityEngine.Behaviour b && b.isActiveAndEnabled));
        }

        return snapshot;
    }

    /// <summary>
    /// The cached container if it is still alive, otherwise one fresh (expensive)
    /// scan. Timed and counted, because how often this is still paid — and what a
    /// single scan costs on this machine — is the whole question when the game
    /// feels less than sharp.
    /// </summary>
    private object? ResolveCurrentContentContainer()
    {
        // A destroyed Unity object compares equal to null, which is exactly the
        // "the scene replaced it" signal we want.
        if (_cachedContainer is UnityEngine.Object cached && cached != null)
            return _cachedContainer;

        var type = _discovery.TryResolveKnownType("CurrentContentContainer");
        var stopwatch = Stopwatch.StartNew();
        var found = ReflectionHelper.GetLiveObjects(type);
        stopwatch.Stop();

        _containerScans++;
        _containerScanTotalMs += stopwatch.ElapsedMilliseconds;
        _cachedContainer = found.Count > 0 ? found[0] : null;

        // Warn rather than Info: this needs to reach the BepInEx log, where it can
        // actually be read, and it is bounded to one line a minute.
        var now = DateTime.UtcNow;
        if (now >= _nextScanReportUtc)
        {
            _nextScanReportUtc = now.AddMinutes(1);
            _log.Warn("HOST", $"CurrentContentContainer object scan: {_containerScans} so far, " +
                $"{_containerScanTotalMs}ms of main thread total, last {stopwatch.ElapsedMilliseconds}ms " +
                $"over {found.Count} candidate(s). Each one is a full Resources.FindObjectsOfTypeAll.");
        }

        return _cachedContainer;
    }

    public void LogSnapshot(string category)
    {
        var snapshot = Capture();
        _log.Info(category, $"IsInMultiplayer={snapshot.IsInMultiplayer} reason={snapshot.InMultiplayerReason}");
        _log.Info(category, $"IsHost={snapshot.IsHost} reason={snapshot.HostReason}");

        if (snapshot.CurrentRoom != null)
        {
            _log.Info(category, $"Room name={snapshot.CurrentRoom.Name} players={snapshot.CurrentRoom.PlayerCount}/{snapshot.CurrentRoom.MaxPlayers} open={snapshot.CurrentRoom.IsOpen} visible={snapshot.CurrentRoom.IsVisible}");
            _log.Info(category, $"Room custom properties={ReflectionHelper.SafeDescribe(_describe, snapshot.RoomProperties)}");
        }

        if (snapshot.SessionSettings != null)
            _log.Info(category, $"Session settings={ReflectionHelper.SafeDescribe(_describe, snapshot.SessionSettings)}");
        if (snapshot.Track != null)
            _log.Info(category, $"Current track={ReflectionHelper.SafeDescribe(_describe, snapshot.Track)}");
        if (snapshot.Race != null)
            _log.Info(category, $"Current race={ReflectionHelper.SafeDescribe(_describe, snapshot.Race)}");
    }

    /// <summary>
    /// Detects whether we're in the lobby waiting room (in a Photon room but not loaded into the game scene).
    /// When Level is null but we're InRoom, we're likely in the lobby waiting screen.
    /// </summary>
    public bool IsInLobbyWaitingRoom()
    {
        if (!PhotonNetwork.InRoom)
            return false;

        var snapshot = Capture();
        // In lobby waiting room: we're in a Photon room but the Level/game scene hasn't loaded
        // CurrentContentContainer.InMultiplayer may be false when sitting in lobby pre-load
        return snapshot.Level == null;
    }

    internal sealed class HostStateSnapshot
    {
        public bool IsInMultiplayer { get; set; }
        public bool IsHost { get; set; }
        public bool IsInLobbyWaitingRoom { get; set; }
        public bool IsOnMultiplayerLobbyScreen { get; set; }
        public bool IsPhotonConnected { get; set; }
        public string PhotonClientState { get; set; } = string.Empty;
        public string InMultiplayerReason { get; set; } = string.Empty;
        public string HostReason { get; set; } = string.Empty;
        public object? CurrentContentContainer { get; set; }
        public object? SessionSettings { get; set; }
        public object? Level { get; set; }
        public object? Track { get; set; }
        public object? Race { get; set; }
        public Room? CurrentRoom { get; set; }
        public Player? LocalPlayer { get; set; }
        public Hashtable? RoomProperties { get; set; }
    }
}
