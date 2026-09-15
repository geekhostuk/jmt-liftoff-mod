using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;

namespace JmtLiftoffMod.Features.Diagnostics;

/// <summary>
/// In-game IMGUI overlay showing diagnostic stats about the plugin's state:
/// connection health, event queue, recent events, and room info.
/// Toggle with a configurable hotkey (default Ctrl+F5).
/// </summary>
internal sealed class BotStatsOverlay
{
    private readonly ManualLogSource _log;
    private readonly CompetitionStats _stats;
    private readonly Func<string> _getRaceId;
    private readonly Func<int> _getRaceOrdinal;
    private readonly Func<int> _getParticipantCount;
    private readonly ConfigEntry<bool> _showOverlay;
    private readonly ConfigEntry<KeyboardShortcut> _toggleHotkey;

    private bool _visible;

    /// <summary>Whether the overlay is showing, so its IMGUI host can be off the rest of the time.</summary>
    public bool Visible => _visible;

    private Rect _windowRect = new(20, 20, 420, 520);
    private Vector2 _scrollPos;

    // Cached snapshots (refresh every 0.5s to avoid per-frame overhead)
    private float _nextRefresh;
    private Dictionary<string, int> _eventCountsCache = new();
    private string[] _recentEventsCache = Array.Empty<string>();

    public BotStatsOverlay(
        ManualLogSource log,
        CompetitionStats stats,
        ConfigEntry<bool> showOverlay,
        ConfigEntry<KeyboardShortcut> toggleHotkey,
        Func<string> getRaceId,
        Func<int> getRaceOrdinal,
        Func<int> getParticipantCount)
    {
        _log = log;
        _stats = stats;
        _showOverlay = showOverlay;
        _toggleHotkey = toggleHotkey;
        _getRaceId = getRaceId;
        _getRaceOrdinal = getRaceOrdinal;
        _getParticipantCount = getParticipantCount;
        _visible = showOverlay.Value;
    }

    /// <summary>Call from Plugin.Update() to check for hotkey toggle.</summary>
    public void Update()
    {
        if (_toggleHotkey.Value.IsDown())
        {
            _visible = !_visible;
            _log.LogInfo($"[StatsOverlay] Toggled {(_visible ? "on" : "off")}");
        }
    }

    /// <summary>Call from Plugin.OnGUI() to render the overlay.</summary>
    public void OnGUI()
    {
        if (!_visible) return;

        // Refresh cached data periodically
        if (Time.time >= _nextRefresh)
        {
            _nextRefresh = Time.time + 0.5f;
            _eventCountsCache = _stats.GetEventCountsSnapshot();
            _recentEventsCache = _stats.GetRecentEventsSnapshot();
        }

        _windowRect = GUILayout.Window(
            94827, // unique window ID
            _windowRect,
            DrawWindow,
            "Bot Stats",
            GUILayout.MinWidth(400),
            GUILayout.MinHeight(200));
    }

    private void DrawWindow(int windowId)
    {
        _scrollPos = GUILayout.BeginScrollView(_scrollPos);

        // ── Connection ────────────────────────────────────────
        GUILayout.Label("<b>Connection</b>", RichStyle());
        var connColor = _stats.IsConnected ? "lime" : "red";
        var connText = _stats.IsConnected ? "CONNECTED" : "DISCONNECTED";
        GUILayout.Label($"  Status: <color={connColor}><b>{connText}</b></color>", RichStyle());
        if (_stats.LastSendUtc > DateTime.MinValue)
            GUILayout.Label($"  Last send:    {FormatAge(_stats.LastSendUtc)}");
        if (_stats.LastReceiveUtc > DateTime.MinValue)
            GUILayout.Label($"  Last receive: {FormatAge(_stats.LastReceiveUtc)}");
        if (!string.IsNullOrEmpty(_stats.LastCommand))
            GUILayout.Label($"  Last command: {_stats.LastCommand}");

        GUILayout.Space(4);

        // ── Queue ─────────────────────────────────────────────
        GUILayout.Label("<b>Queue</b>", RichStyle());
        GUILayout.Label($"  Outbox depth: {_stats.OutboxDepth}");
        GUILayout.Label($"  Enqueued:     {_stats.TotalEventsEnqueued}");
        GUILayout.Label($"  Sent:         {_stats.TotalEventsSent}");
        if (_stats.TotalEventsDropped > 0)
            GUILayout.Label($"  <color=yellow>Dropped:      {_stats.TotalEventsDropped}</color>", RichStyle());

        GUILayout.Space(4);

        // ── Race State ────────────────────────────────────────
        GUILayout.Label("<b>Race</b>", RichStyle());
        GUILayout.Label($"  Race ID:      {_getRaceId().Substring(0, Math.Min(12, _getRaceId().Length))}...");
        GUILayout.Label($"  Race ordinal: {_getRaceOrdinal()}");
        GUILayout.Label($"  Participants: {_getParticipantCount()}");

        GUILayout.Space(4);

        // ── Room State ────────────────────────────────────────
        GUILayout.Label("<b>Room</b>", RichStyle());
        if (PhotonNetwork.InRoom)
        {
            var room = PhotonNetwork.CurrentRoom;
            GUILayout.Label($"  Name:    {room?.Name ?? "?"}");
            GUILayout.Label($"  Players: {room?.PlayerCount ?? 0}/{room?.MaxPlayers ?? 0}");
            GUILayout.Label($"  Host:    {(PhotonNetwork.IsMasterClient ? "YES" : "no")}");
            GUILayout.Label($"  Open:    {(room?.IsOpen == true ? "yes" : "no")}");
        }
        else
        {
            GUILayout.Label($"  <color=yellow>Not in room</color>", RichStyle());
            GUILayout.Label($"  State: {PhotonNetwork.NetworkClientState}");
        }

        GUILayout.Space(4);

        // ── Event Counts ──────────────────────────────────────
        GUILayout.Label("<b>Event Counts</b>", RichStyle());
        if (_eventCountsCache.Count > 0)
        {
            foreach (var kv in _eventCountsCache.OrderByDescending(x => x.Value))
            {
                GUILayout.Label($"  {kv.Key}: {kv.Value}");
            }
        }
        else
        {
            GUILayout.Label("  (none yet)");
        }

        GUILayout.Space(4);

        // ── Recent Events ─────────────────────────────────────
        GUILayout.Label("<b>Recent Events</b>", RichStyle());
        if (_recentEventsCache.Length > 0)
        {
            for (int i = _recentEventsCache.Length - 1; i >= 0; i--)
            {
                GUILayout.Label($"  {_recentEventsCache[i]}");
            }
        }
        else
        {
            GUILayout.Label("  (none yet)");
        }

        GUILayout.EndScrollView();
        GUI.DragWindow();
    }

    private static GUIStyle RichStyle()
    {
        var style = new GUIStyle(GUI.skin.label) { richText = true };
        return style;
    }

    private static string FormatAge(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        if (age.TotalSeconds < 60) return $"{age.TotalSeconds:0}s ago";
        if (age.TotalMinutes < 60) return $"{age.TotalMinutes:0}m ago";
        return $"{age.TotalHours:0.0}h ago";
    }
}
