using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace JmtLiftoffMod.Features.Racing;

/// <summary>A drone followed from the start after a respawn: when it left, or null when it never did.</summary>
internal readonly struct StartSeen
{
    public StartSeen(DateTime? leftAt)
    {
        LeftAt = leftAt;
    }

    public DateTime? LeftAt { get; }
}

/// <summary>
/// Whether each pilot's drone has left the start since it last respawned, and when.
///
/// After a respawn the drone waits at the course's spawn point until the pilot arms it, and
/// then the countdown runs. Timing an abandoned attempt from the respawn counted all of that,
/// so a pilot who sat on the line and then reset was charged a failed attempt. So the drones
/// are followed: an attempt after a respawn starts when the drone gets further than
/// <see cref="StartMetres"/> from the spawn point.
///
/// Each pilot's drone is the PhotonView whose id is their <c>DID</c> player property, the
/// host's own included; the spawn point is <c>CurrentContentContainer.DroneSpawnPoint</c>. A
/// remote drone is played back a fraction of a second late, and its copy stays where it
/// crashed until the first update from the start, so after a respawn the drone has to be seen
/// at the start before leaving it counts. When it is never seen there -- no spawn point, no
/// drone -- nothing is known, and the reset is timed from the respawn as it always was.
/// </summary>
internal sealed class StartWatch
{
    /// <summary>How far from the spawn point a drone has left the start.</summary>
    private const float StartMetres = 5f;

    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ContainerLookEvery = TimeSpan.FromSeconds(2);

    private enum Stage
    {
        Respawning,
        AtStart,
        Left,
    }

    private sealed class Pilot
    {
        public Stage Stage;
        public DateTime? LeftAt;
    }

    private readonly Action<string> _log;
    private readonly Dictionary<int, Pilot> _pilots = new();
    private readonly Dictionary<int, PhotonView> _views = new();
    private DateTime _nextPoll;
    private Type? _containerType;
    private PropertyInfo? _spawnProperty;
    private UnityEngine.Object? _container;
    private DateTime _nextContainerLook;
    private bool _saidFollowing;
    private bool _failed;

    public StartWatch(Action<string> log)
    {
        _log = log;
    }

    /// <summary>
    /// A pilot's drone has just respawned. Returns how the attempt it ended began, or null when
    /// the drone wasn't followed from the start. The pilot is followed from here on.
    /// </summary>
    public StartSeen? Respawned(int actor)
    {
        StartSeen? began = null;
        if (_pilots.TryGetValue(actor, out var pilot) && pilot.Stage != Stage.Respawning)
            began = new StartSeen(pilot.LeftAt);
        _pilots[actor] = new Pilot { Stage = Stage.Respawning };
        return began;
    }

    public void Forget(int actor)
    {
        _pilots.Remove(actor);
        _views.Remove(actor);
    }

    public void Clear()
    {
        _pilots.Clear();
        _views.Clear();
    }

    /// <summary>Called every frame; looks at the drones ten times a second while any is at the start.</summary>
    public void Poll(DateTime now)
    {
        if (_failed || now < _nextPoll || !PhotonNetwork.InRoom)
            return;
        _nextPoll = now + PollEvery;
        try
        {
            if (!AnyWaiting())
                return;
            var spawn = SpawnPoint(now);
            var room = PhotonNetwork.CurrentRoom;
            if (spawn == null || room == null)
                return;
            foreach (var entry in room.Players)
            {
                if (!_pilots.TryGetValue(entry.Key, out var pilot) || pilot.Stage == Stage.Left)
                    continue;
                if (DronePosition(entry.Value) is not { } at)
                    continue;
                var away = Vector3.Distance(at, spawn.Value);
                if (pilot.Stage == Stage.Respawning)
                {
                    if (away <= StartMetres)
                    {
                        pilot.Stage = Stage.AtStart;
                        if (!_saidFollowing)
                        {
                            _saidFollowing = true;
                            _log($"[Start] Following drones from the start at {spawn.Value}: a reset after a respawn is timed from when the drone leaves it.");
                        }
                    }
                }
                else if (away > StartMetres)
                {
                    pilot.Stage = Stage.Left;
                    pilot.LeftAt = now;
                }
            }
        }
        catch (Exception ex)
        {
            // Every lookup here is the game's, and a game update can move any of them.
            _failed = true;
            _log($"[Start] Following drones failed, so resets are timed from the respawn: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool AnyWaiting()
    {
        foreach (var pilot in _pilots.Values)
        {
            if (pilot.Stage != Stage.Left)
                return true;
        }
        return false;
    }

    /// <summary>Where a respawned drone is put, or null while there is no course.</summary>
    private Vector3? SpawnPoint(DateTime now)
    {
        if (_container == null)
        {
            // Finding it walks every loaded object, so a course that isn't there is looked
            // for every couple of seconds, not ten times a second. It outlives track changes.
            if (now < _nextContainerLook)
                return null;
            _nextContainerLook = now + ContainerLookEvery;
            _containerType ??= AccessTools.TypeByName("CurrentContentContainer");
            _spawnProperty ??= _containerType == null ? null : AccessTools.Property(_containerType, "DroneSpawnPoint");
            if (_containerType == null || _spawnProperty == null)
                return null;
            foreach (var found in Resources.FindObjectsOfTypeAll(_containerType))
            {
                if (found != null && _spawnProperty.GetValue(found, null) is Component)
                {
                    _container = found;
                    break;
                }
            }
            if (_container == null)
                return null;
        }
        return _spawnProperty!.GetValue(_container, null) is Component point && point != null
            ? point.transform.position
            : null;
    }

    /// <summary>The pilot's drone, by its view id; or failing that, the view they own that is named for a drone.</summary>
    private Vector3? DronePosition(Player player)
    {
        if (player.CustomProperties != null && player.CustomProperties.TryGetValue("DID", out var did) && did is int id && id > 0)
        {
            var byId = PhotonView.Find(id);
            if (byId != null)
                return byId.transform.position;
        }
        if (!_views.TryGetValue(player.ActorNumber, out var view) || view == null)
        {
            view = null;
            foreach (var candidate in PhotonNetwork.PhotonViewCollection)
            {
                if (candidate != null && candidate.OwnerActorNr == player.ActorNumber
                    && candidate.gameObject.name.StartsWith("Drone_", StringComparison.Ordinal))
                {
                    view = candidate;
                    break;
                }
            }
            if (view == null)
                return null;
            _views[player.ActorNumber] = view;
        }
        return view.transform.position;
    }
}
