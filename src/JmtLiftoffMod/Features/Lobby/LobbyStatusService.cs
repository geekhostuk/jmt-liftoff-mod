using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Photon.Pun;

namespace JmtLiftoffMod.Features.Lobby;

/// <summary>
/// Builds lobby_status event payloads from current Photon room state.
/// Called on-demand (server request) and automatically on state changes.
/// </summary>
internal sealed class LobbyStatusService
{
    private readonly ManualLogSource _log;
    private readonly Action<string, Dictionary<string, object?>> _emitEvent;
    private readonly Func<string?>? _getScreenState;

    public LobbyStatusService(ManualLogSource log, Action<string, Dictionary<string, object?>> emitEvent, Func<string?>? getScreenState = null)
    {
        _log = log;
        _emitEvent = emitEvent;
        _getScreenState = getScreenState;
    }

    /// <summary>
    /// Emits a lobby_status event with the current Photon room state.
    /// Must be called on the Unity main thread.
    /// </summary>
    public void EmitLobbyStatus()
    {
        try
        {
            var inRoom = PhotonNetwork.InRoom;
            var room = PhotonNetwork.CurrentRoom;

            var payload = new Dictionary<string, object?>
            {
                ["in_room"] = inRoom,
                ["in_lobby"] = PhotonNetwork.InLobby,
                ["room_name"] = room?.Name,
                ["player_count"] = room?.PlayerCount ?? 0,
                ["max_players"] = room?.MaxPlayers ?? 0,
                ["is_host"] = inRoom && PhotonNetwork.IsMasterClient,
                ["is_open"] = room?.IsOpen ?? false,
                ["is_visible"] = room?.IsVisible ?? false,
                ["network_state"] = PhotonNetwork.NetworkClientState.ToString(),
                ["screen_state"] = _getScreenState?.Invoke() ?? "unknown",
            };

            // Include current track info if in a room
            if (inRoom && room?.CustomProperties != null)
            {
                if (room.CustomProperties.TryGetValue("E", out var e))
                    payload["current_env"] = e as string ?? "";
                if (room.CustomProperties.TryGetValue("T", out var t))
                {
                    var trackName = MultiplayerTrackControl.ReflectionHelper.GetMemberValue(t, "Name") as string ?? t?.ToString() ?? "";
                    payload["current_track"] = trackName;
                }
                if (room.CustomProperties.TryGetValue("R", out var r))
                {
                    var raceName = MultiplayerTrackControl.ReflectionHelper.GetMemberValue(r, "Name") as string ?? r?.ToString() ?? "";
                    payload["current_race"] = raceName;
                }
            }

            _emitEvent("lobby_status", payload);
            _log.LogInfo($"[Lobby] Status emitted: in_room={inRoom} room={room?.Name} players={room?.PlayerCount ?? 0}");
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Lobby] Failed to emit status: {ex.Message}");
        }
    }
}
