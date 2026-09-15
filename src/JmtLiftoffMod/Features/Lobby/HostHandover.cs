using System;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;

namespace JmtLiftoffMod.Features.Lobby;

/// <summary>
/// Hands the room's host on as this game leaves it: to the other player with the lowest actor
/// number whose plugin has a controller connected (JMTC), so a controller is there to carry
/// the room's playlist on. Only a normal leave can do this. Liftoff leaves a room, and
/// disconnects, through PhotonNetwork, which is not obfuscated, so both are patched there;
/// quitting the game calls <see cref="HandOn"/> from the plugin. After a crash or a lost
/// connection Photon picks the next host itself.
/// </summary>
internal sealed class HostHandover : IDisposable
{
    // Harmony patches are static, so the way back to the instance has to be too. Everything
    // here runs on the Unity main thread.
    private static HostHandover? s_current;

    private readonly ManualLogSource _log;
    private readonly Harmony _harmony;
    private bool _installed;
    // The room and actor this game last handed on, so a room is handed on once at most: a
    // leave and the quit that follows it both come here.
    private string? _handedOn;

    public HostHandover(ManualLogSource log, string harmonyId)
    {
        _log = log;
        _harmony = new Harmony(harmonyId + ".handover");
    }

    public void Install()
    {
        if (_installed) return;
        s_current = this;
        try
        {
            var leave = AccessTools.Method(typeof(PhotonNetwork), nameof(PhotonNetwork.LeaveRoom), new[] { typeof(bool) });
            var disconnect = AccessTools.Method(typeof(PhotonNetwork), nameof(PhotonNetwork.Disconnect), Type.EmptyTypes);

            if (leave != null)
                _harmony.Patch(leave, prefix: new HarmonyMethod(typeof(HostHandover), nameof(LeaveRoomPrefix)));
            else
                _log.LogWarning("[Host] PhotonNetwork.LeaveRoom(bool) not found: host is not handed on when the game leaves a room.");

            if (disconnect != null)
                _harmony.Patch(disconnect, prefix: new HarmonyMethod(typeof(HostHandover), nameof(DisconnectPrefix)));
            else
                _log.LogWarning("[Host] PhotonNetwork.Disconnect() not found: host is not handed on when the game disconnects.");

            _installed = true;
            _log.LogInfo("[Host] Handing host on is installed.");
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Host] Could not install handing host on: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (s_current == this) s_current = null;
        if (!_installed) return;
        try { _harmony.UnpatchSelf(); } catch { /* shutting down */ }
        _installed = false;
    }

    private static void LeaveRoomPrefix() => s_current?.HandOn("Leaving the room");
    private static void DisconnectPrefix() => s_current?.HandOn("Disconnecting from Photon");

    /// <summary>
    /// If this game hosts the room, makes the other player with the lowest actor number whose
    /// JMTC is 1 the host, and sends that at once. Does nothing without such a player, and
    /// never picks this game. Main thread only.
    /// </summary>
    public void HandOn(string why)
    {
        try
        {
            if (!PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient)
                return;
            var room = PhotonNetwork.CurrentRoom;
            var local = PhotonNetwork.LocalPlayer;
            if (room?.Players == null || local == null)
                return;
            var key = $"{room.Name}#{local.ActorNumber}";
            if (_handedOn == key)
                return;

            Player? next = null;
            foreach (var player in room.Players.Values)
            {
                if (player == null || player.IsLocal || player.ActorNumber == local.ActorNumber || player.IsInactive)
                    continue;
                if (!RoomPlaylist.HasController(player))
                    continue;
                if (next == null || player.ActorNumber < next.ActorNumber)
                    next = player;
            }
            if (next == null)
            {
                _log.LogInfo($"[Host] {why}: no other pilot has a controller connected, so Photon picks the next host.");
                return;
            }

            _handedOn = key;
            _log.LogInfo($"[Host] {why}: handing host to {next.NickName} (#{next.ActorNumber}), whose plugin has a controller connected.");
            if (!PhotonNetwork.SetMasterClient(next))
            {
                _log.LogWarning($"[Host] Photon would not hand host to {next.NickName} (#{next.ActorNumber}): it picks the next host itself.");
                return;
            }
            // Out now, ahead of the leave that follows on the same reliable channel. A
            // disconnect drops whatever is still queued, and quitting disconnects this frame.
            PhotonNetwork.SendAllOutgoingCommands();
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Host] Handing host on failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
