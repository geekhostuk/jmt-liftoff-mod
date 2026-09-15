using System.Collections.Generic;
using System.Text;
using Photon.Pun;
using Photon.Realtime;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;

namespace JmtLiftoffMod.Features.Lobby;

/// <summary>
/// The controller's playlist state, kept on the Photon room so it outlives the room's host,
/// and the player property that says a pilot's plugin has a controller connected. The state
/// is an opaque string owned by the controller: the plugin stores it and hands it back, and
/// never reads it. See docs/server-protocol.md, "Room playlist and handover".
/// </summary>
internal static class RoomPlaylist
{
    /// <summary>Room property: the controller's playlist state.</summary>
    public const string StateKey = "JMTP";

    /// <summary>Room property: <c>PhotonNetwork.ServerTimestamp</c> when the state was written.</summary>
    public const string WrittenAtKey = "JMTPt";

    /// <summary>Player property: 1 while that pilot's plugin has a controller connected.</summary>
    public const string ControllerKey = "JMTC";

    public const int MaxStateBytes = 2048;

    /// <summary>
    /// Stores the state on the room, stamped with the server's time, in one call. False, with
    /// the reason, when it can't. Main thread only.
    /// </summary>
    public static bool TryWrite(string? state, out string error)
    {
        var room = PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom : null;
        if (room == null)
        {
            error = "not in a room";
            return false;
        }
        if (state == null)
        {
            error = "missing state";
            return false;
        }
        if (Encoding.UTF8.GetByteCount(state) > MaxStateBytes)
        {
            error = "state too long";
            return false;
        }
        if (!room.SetCustomProperties(new PhotonHashtable
            {
                [StateKey] = state,
                [WrittenAtKey] = PhotonNetwork.ServerTimestamp,
            }))
        {
            error = "Photon did not send the room properties";
            return false;
        }
        error = "";
        return true;
    }

    /// <summary>The <c>room_playlist</c> payload: the room's state and how old it is. Main thread only.</summary>
    public static Dictionary<string, object?> Snapshot()
    {
        var inRoom = PhotonNetwork.InRoom;
        string? state = null;
        int? ageMs = null;
        var props = inRoom ? PhotonNetwork.CurrentRoom?.CustomProperties : null;
        // Anyone in a room can write its properties: anything but what set_room_playlist
        // writes counts as no state.
        if (props != null && props.TryGetValue(StateKey, out var raw) && raw is string s)
        {
            state = s;
            if (props.TryGetValue(WrittenAtKey, out var at) && at is int writtenAt)
                ageMs = unchecked(PhotonNetwork.ServerTimestamp - writtenAt);
        }
        return new Dictionary<string, object?>
        {
            ["in_room"] = inRoom,
            ["is_host"] = inRoom && PhotonNetwork.IsMasterClient,
            ["state"] = state,
            ["age_ms"] = ageMs,
        };
    }

    /// <summary>Whether this player's plugin says it has a controller connected.</summary>
    public static bool HasController(Player? player) =>
        player?.CustomProperties != null
        && player.CustomProperties.TryGetValue(ControllerKey, out var value)
        && value switch
        {
            int i => i == 1,
            byte b => b == 1,
            short sh => sh == 1,
            long l => l == 1,
            _ => false,
        };

    /// <summary>
    /// Sets or removes JMTC on the local player. Outside a room Photon keeps it on the player
    /// and sends it with the next room the game joins. Main thread only.
    /// </summary>
    public static bool SetControllerFlag(bool connected)
    {
        var local = PhotonNetwork.LocalPlayer;
        return local != null
            && local.SetCustomProperties(new PhotonHashtable { [ControllerKey] = connected ? (object)1 : null });
    }
}
