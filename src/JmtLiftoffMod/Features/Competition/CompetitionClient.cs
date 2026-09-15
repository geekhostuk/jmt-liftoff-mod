using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using JmtLiftoffMod.Features.Diagnostics;
using JmtLiftoffMod.Features.Lobby;
using JmtLiftoffMod.Features.MultiplayerTrackControl;
using JmtLiftoffMod.Features.Racing;
using Photon.Pun;
using SyncContext = System.Threading.SynchronizationContext;

namespace JmtLiftoffMod.Features.Competition;

/// <summary>
/// Maintains a persistent WebSocket connection to the competition server.
///
/// Threading model:
///   - All WebSocket I/O runs on a background Task.
///   - Events to send are enqueued from the Unity main thread via EnqueueEvent().
///   - Commands received from the server are queued and dispatched on the Unity
///     main thread inside Update().
/// </summary>
internal sealed class CompetitionClient : IDisposable
{
    private readonly CompetitionConfig _config;
    private readonly ManualLogSource _log;
    private readonly MultiplayerTrackControlService _trackControl;
    private readonly SyncContext? _unitySyncContext;
    private readonly Func<int?> _getLocalActor;
    private readonly Func<long>? _getMainThreadLastTickUtcMs;
    private readonly string _sessionId;

    private const int MaxOutboxSize = 10_000;

    private readonly ConcurrentQueue<string> _outbox = new();
    private readonly Action<Dictionary<string, object?>>? _onCatalogReady;
    private readonly Action<int>? _onKickPlayer;
    private readonly Action? _onTrackChanging;
    private readonly Action? _onConnected;
    // A connection that had opened has closed. Not called for attempts that never connected.
    private readonly Action? _onDisconnected;
    private readonly Action? _onLobbyStatusRequested;
    // True while this game is a guest in someone else's room: only the host's copy acts.
    private readonly Func<bool>? _isRoomGuest;

    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private bool _disposed;

    // Cached room track info (updated on main thread, read by keepalive on background thread)
    private volatile string _cachedRoomTrackJson = "";

    // Race context pushed from Plugin for inclusion in keepalive
    private volatile string _raceId = "";
    private volatile int _raceOrdinal;

    /// <summary>Tracks event counts and connection health for the stats overlay.</summary>
    public CompetitionStats Stats { get; } = new();

    public CompetitionClient(
        CompetitionConfig config,
        ManualLogSource log,
        MultiplayerTrackControlService trackControl,
        SyncContext? unitySyncContext,
        Func<int?> getLocalActor,
        string sessionId,
        Action<Dictionary<string, object?>>? onCatalogReady = null,
        Action<int>? onKickPlayer = null,
        Action? onTrackChanging = null,
        Action? onConnected = null,
        Action? onLobbyStatusRequested = null,
        Func<long>? getMainThreadLastTickUtcMs = null,
        Func<bool>? isRoomGuest = null,
        Action? onDisconnected = null)
    {
        _config = config;
        _log = log;
        _trackControl = trackControl;
        _unitySyncContext = unitySyncContext;
        _getLocalActor = getLocalActor;
        _getMainThreadLastTickUtcMs = getMainThreadLastTickUtcMs;
        _sessionId = sessionId;
        _onCatalogReady = onCatalogReady;
        _onKickPlayer = onKickPlayer;
        _onTrackChanging = onTrackChanging;
        _onConnected = onConnected;
        _onLobbyStatusRequested = onLobbyStatusRequested;
        _isRoomGuest = isRoomGuest;
        _onDisconnected = onDisconnected;
    }

    /// <summary>
    /// Called by Plugin when a new race starts so the keepalive includes current race context.
    /// </summary>
    public void SetRaceContext(string raceId, int raceOrdinal)
    {
        _raceId = raceId;
        _raceOrdinal = raceOrdinal;
    }

    public void Start()
    {
        if (!_config.Enabled.Value)
        {
            _log.LogInfo("[Competition] Disabled.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.ApiKey.Value))
        {
            _log.LogWarning("[Competition] No API key set — paste your bot's API key into Competition.ApiKey in the config, then restart Liftoff.");
            return;
        }

        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
        _log.LogInfo($"[Competition] Client started. Target: {_config.ServerUrl.Value}");
    }

    /// <summary>No longer needed — kept for compatibility. Commands now use SynchronizationContext.</summary>
    private DateTime _lastRoomTrackCacheUtc = DateTime.MinValue;

    public void Update()
    {
        // Refresh room track cache every 5 seconds for keepalive background thread
        var now = DateTime.UtcNow;
        if ((now - _lastRoomTrackCacheUtc).TotalSeconds >= 5)
        {
            _lastRoomTrackCacheUtc = now;
            ReadAndCacheRoomTrackJson();
        }
    }

    /// <summary>
    /// Reads the room now rather than at the next five-second refresh. Called as this game
    /// becomes or stops being the room's host, so the next keepalive can't say otherwise.
    /// Main thread only.
    /// </summary>
    public void RefreshRoomState()
    {
        _lastRoomTrackCacheUtc = DateTime.UtcNow;
        ReadAndCacheRoomTrackJson();
    }

    /// <summary>Forward a JSONL event line to the server. Safe to call from any thread.</summary>
    public void EnqueueEvent(string jsonLine)
    {
        if (_config.Enabled.Value && _outbox.Count < MaxOutboxSize)
        {
            _outbox.Enqueue(jsonLine);
            Stats.TotalEventsEnqueued++;
            Stats.RecordEvent(jsonLine);
        }
        else if (_config.Enabled.Value)
        {
            Stats.TotalEventsDropped++;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        try { _workerTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutdown timeout */ }
        _cts?.Dispose();
        _cts = null;
    }

    // ── Background worker ─────────────────────────────────────────────────

    private async Task WorkerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndRunAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[Competition] Disconnected: {ex.Message}. Reconnecting in {_config.ReconnectDelaySecs.Value}s...");
            }

            if (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(_config.ReconnectDelaySecs.Value), ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        _log.LogInfo("[Competition] Worker stopped.");
    }

    private async Task ConnectAndRunAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {_config.ApiKey.Value}");

        var uri = new Uri(_config.ServerUrl.Value);
        _log.LogInfo($"[Competition] Connecting to {uri}...");

        await ws.ConnectAsync(uri, ct);
        _log.LogInfo("[Competition] Connected.");
        RunOnMainThread(() => _onConnected?.Invoke());

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var sendTask    = SendLoopAsync(ws, linkedCts.Token);
            var receiveTask = ReceiveLoopAsync(ws, linkedCts.Token);

            // If either loop exits, cancel the other and exit so we reconnect
            await Task.WhenAny(sendTask, receiveTask);
            linkedCts.Cancel();

            try { await Task.WhenAll(sendTask, receiveTask); } catch { /* swallow */ }

            _log.LogInfo("[Competition] Connection closed.");
        }
        finally
        {
            // Nothing is connected until the next connect succeeds. Skipped once disposed.
            RunOnMainThread(() => _onDisconnected?.Invoke());
        }
    }

    private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var lastKeepaliveUtc = DateTime.UtcNow;
        Stats.IsConnected = true;

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            while (_outbox.TryDequeue(out var line))
            {
                var bytes = Encoding.UTF8.GetBytes(line);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                Stats.TotalEventsSent++;
                Stats.LastSendUtc = DateTime.UtcNow;
            }

            Stats.OutboxDepth = _outbox.Count;

            // Emit keep-alive to prevent idle-kick while in lobby waiting room
            var now = DateTime.UtcNow;
            var intervalSecs = _config.KeepAliveIntervalSecs.Value;
            if ((now - lastKeepaliveUtc).TotalSeconds >= intervalSecs)
            {
                lastKeepaliveUtc = now;
                var actor = _getLocalActor();
                if (actor.HasValue)
                {
                    var roomTrack = _cachedRoomTrackJson;
                    var raceId = _raceId;
                    var raceOrd = _raceOrdinal;
                    // Main-thread liveness enrichment. If Plugin hasn't wired the delegate
                    // yet (early startup) emit 0 and -1 so the server can tell "no data".
                    long mainThreadLastTick = _getMainThreadLastTickUtcMs?.Invoke() ?? 0;
                    long mainThreadGapMs = mainThreadLastTick > 0
                        ? Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - mainThreadLastTick)
                        : -1;
                    var keepalive = $"{{\"event_type\":\"keepalive\",\"timestamp_utc\":\"{now:O}\",\"session_id\":\"{_sessionId}\",\"actor\":{actor.Value},\"race_id\":\"{raceId}\",\"race_ordinal\":{raceOrd},\"main_thread_last_tick_utc_ms\":{mainThreadLastTick},\"main_thread_gap_ms\":{mainThreadGapMs}{roomTrack}}}";
                    var bytes = Encoding.UTF8.GetBytes(keepalive);
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                    _log.LogDebug($"[Competition] Keep-alive sent for actor {actor.Value} mainThreadGapMs={mainThreadGapMs}");
                }
            }

            // Small yield to avoid spinning
            await Task.Delay(50, ct);
        }

        Stats.IsConnected = false;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var messageBuffer = new StringBuilder();

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Acknowledged", ct);
                return;
            }

            messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            if (result.EndOfMessage)
            {
                var text = messageBuffer.ToString().Trim();
                messageBuffer.Clear();
                if (text.Length > 0)
                {
                    Stats.LastReceiveUtc = DateTime.UtcNow;
                    DispatchCommand(text);
                }
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    // Staleness threshold (ms) before a queued main-thread command is skipped
    // on pickup. Matches our Photon DisconnectTimeout so a command queued during
    // a long main-thread hang is treated as irrelevant once the hang ends.
    private const long StaleCommandThresholdMs = 30000;

    /// <summary>
    /// Post an action to the Unity main thread and, on pickup, skip it if the
    /// age exceeds <see cref="StaleCommandThresholdMs"/>. Used for server-issued
    /// commands like set_track that become meaningless after a long hang.
    /// </summary>
    private void ScheduleStaleable(string cmd, string? commandId, Action action)
    {
        if (_disposed) return;
        var enqueueMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        RunOnMainThread(() =>
        {
            var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - enqueueMs;
            if (ageMs > StaleCommandThresholdMs)
            {
                _log.LogWarning($"[Competition] Skipped stale command cmd={cmd} id={commandId} ageMs={ageMs}");
                EmitCommandAck(commandId, "skipped_stale", $"ageMs={ageMs}");
                return;
            }
            action();
        });
    }

    private void RunOnMainThread(Action action)
    {
        if (_disposed) return;
        if (_unitySyncContext != null)
        {
            _unitySyncContext.Post(_ =>
            {
                using var probe = FrameProbe.Measure(FrameProbe.Part.Command);
                action();
            }, null);
        }
        else
        {
            _log.LogWarning("[Competition] No SyncContext — running command inline on background thread (Unity APIs may fail).");
            action();
        }
    }

    // ── Room track state ───────────────────────────────────────────────────

    /// <summary>
    /// Reads the current track and room state from Photon room properties (must be called on Unity main thread).
    /// Updates the volatile cache for use by the background keepalive thread.
    /// Returns JSON fragment with track, room, and host details.
    /// </summary>
    private string ReadAndCacheRoomTrackJson()
    {
        try
        {
            var room = PhotonNetwork.CurrentRoom;
            if (room?.CustomProperties == null)
            {
                _cachedRoomTrackJson = "";
                return "";
            }

            var env = "";
            var track = "";
            var race = "";
            var workshopId = "";

            if (room.CustomProperties.TryGetValue("E", out var e))
                env = e as string ?? "";
            if (room.CustomProperties.TryGetValue("T", out var t))
                track = ReflectionHelper.GetMemberValue(t, "Name") as string ?? t?.ToString() ?? "";
            if (room.CustomProperties.TryGetValue("R", out var r))
                race = ReflectionHelper.GetMemberValue(r, "Name") as string ?? r?.ToString() ?? "";
            room.CustomProperties.TryGetValue("W", out var w);
            workshopId = RoomTrack.WorkshopIdOf(r, t, w);

            var roomName = room.Name ?? "";
            var playerCount = room.PlayerCount;
            var maxPlayers = room.MaxPlayers;
            var isHost = PhotonNetwork.IsMasterClient;
            var isOpen = room.IsOpen;

            var sb = new StringBuilder(256);
            sb.Append($",\"current_env\":\"{EscapeJsonValue(env)}\"");
            sb.Append($",\"current_track\":\"{EscapeJsonValue(track)}\"");
            sb.Append($",\"current_race\":\"{EscapeJsonValue(race)}\"");
            sb.Append($",\"room_name\":\"{EscapeJsonValue(roomName)}\"");
            sb.Append($",\"player_count\":{playerCount}");
            sb.Append($",\"max_players\":{maxPlayers}");
            sb.Append($",\"is_host\":{(isHost ? "true" : "false")}");
            sb.Append($",\"is_open\":{(isOpen ? "true" : "false")}");
            if (!string.IsNullOrEmpty(workshopId))
                sb.Append($",\"workshop_id\":\"{EscapeJsonValue(workshopId)}\"");

            var json = sb.ToString();
            _cachedRoomTrackJson = json;
            return json;
        }
        catch
        {
            return _cachedRoomTrackJson;
        }
    }

    private static string EscapeJsonValue(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // ── Command acknowledgment ──────────────────────────────────────────────

    private void EmitCommandAck(string? commandId, string status, string message = "", CommandTimingContext? timing = null)
    {
        if (string.IsNullOrEmpty(commandId)) return;
        var roomTrack = ReadAndCacheRoomTrackJson();
        var timingFields = timing != null ? "," + timing.ToAckJsonFields() : "";
        var ack = $"{{\"event_type\":\"command_ack\",\"command_id\":\"{commandId}\",\"status\":\"{status}\",\"message\":\"{message}\"{timingFields}{roomTrack}}}";
        EnqueueEvent(ack);
    }

    /// <summary>
    /// Whether this game is a guest in someone else's room. If so the command is acked
    /// <c>not_host</c> and nothing else happens -- no grace window, no race, no change -- since
    /// only the host's copy changes tracks, chats or kicks. Checked on the main thread, before
    /// anything the command would do.
    /// </summary>
    private bool RefuseAsGuest(string cmd, string? commandId)
    {
        if (_isRoomGuest?.Invoke() != true)
            return false;
        _log.LogInfo($"[Competition] {cmd} refused: this game is a guest in the room, and only the host's copy acts.");
        EmitCommandAck(commandId, "not_host", "this game is not the room's host");
        return true;
    }

    // ── Command dispatch ───────────────────────────────────────────────────

    private void DispatchCommand(string json)
    {
        var fields = SimpleJsonParser.TryParseObject(json);
        if (fields == null || !fields.TryGetValue("cmd", out var cmd))
        {
            _log.LogWarning($"[Competition] Received unrecognised message: {json.Substring(0, Math.Min(120, json.Length))}");
            return;
        }

        fields.TryGetValue("command_id", out var commandId);
        Stats.LastCommand = cmd ?? "";

        switch (cmd)
        {
            case "next_track":
                _log.LogInfo("[Competition] next_track received — scheduling on main thread.");
                ScheduleStaleable("next_track", commandId, () =>
                {
                    if (RefuseAsGuest("next_track", commandId)) return;
                    try
                    {
                        _onTrackChanging?.Invoke();
                        _trackControl.ExternalCycleNext("competition-server");
                        EmitCommandAck(commandId, "ok");
                    }
                    catch (Exception ex) { _log.LogWarning($"[Competition] next_track failed: {ex.GetType().Name}: {ex.Message}"); EmitCommandAck(commandId, "error", ex.Message); }
                });
                break;

            case "set_track":
                fields.TryGetValue("env",        out var env);
                fields.TryGetValue("track",       out var track);
                fields.TryGetValue("race",        out var race);
                fields.TryGetValue("workshop_id", out var workshopId);
                var setTrackTiming = new CommandTimingContext(commandId, "set_track");
                _log.LogInfo($"[Competition] set_track received — env={env} track={track} race={race} command_id={commandId}");
                ScheduleStaleable("set_track", commandId, () =>
                {
                    setTrackTiming.MarkMainThreadPickup();
                    _log.LogInfo($"[timing] set_track main-thread pickup command_id={commandId} queueDelay={setTrackTiming.QueueDelayMs}ms");
                    if (RefuseAsGuest("set_track", commandId)) return;
                    try
                    {
                        _onTrackChanging?.Invoke();
                        _trackControl.ExternalSetTrack(env ?? "", track ?? "", race ?? "", workshopId ?? "", "competition-server", setTrackTiming);
                        setTrackTiming.EndCurrentPhase();
                        _log.LogInfo($"[timing] set_track complete command_id={commandId} {setTrackTiming.GetSummary()}");
                        EmitCommandAck(commandId, "ok", "", setTrackTiming);
                    }
                    catch (Exception ex) { _log.LogWarning($"[Competition] set_track failed: {ex.GetType().Name}: {ex.Message}"); EmitCommandAck(commandId, "error", ex.Message, setTrackTiming); }
                });
                break;

            case "prepare_track":
                // NOTE: prepare_track removed — warmup was discarded by set_track.
                // ACK immediately so the server doesn't time out if running an older version.
                _log.LogInfo($"[Competition] prepare_track received (no-op) command_id={commandId}");
                EmitCommandAck(commandId, "ok", "no-op");
                break;

            case "update_playlist":
                fields.TryGetValue("sequence", out var sequence);
                var applyImmediately = fields.TryGetValue("apply_immediately", out var applyVal)
                    && string.Equals(applyVal, "true", StringComparison.OrdinalIgnoreCase);
                _log.LogInfo($"[Competition] update_playlist received — applyImmediately={applyImmediately}");
                if (!string.IsNullOrWhiteSpace(sequence))
                {
                    var seq = sequence!;
                    ScheduleStaleable("update_playlist", commandId, () =>
                    {
                        // Only jumping to the playlist changes the room; replacing it is harmless.
                        if (applyImmediately && RefuseAsGuest("update_playlist", commandId)) return;
                        try { _trackControl.ExternalUpdatePlaylist(seq, applyImmediately, "competition-server"); EmitCommandAck(commandId, "ok"); }
                        catch (Exception ex) { _log.LogWarning($"[Competition] update_playlist failed: {ex.GetType().Name}: {ex.Message}"); EmitCommandAck(commandId, "error", ex.Message); }
                    });
                }
                break;

            case "set_room_playlist":
                fields.TryGetValue("state", out var roomPlaylist);
                var roomPlaylistSize = roomPlaylist == null ? "no state" : $"{Encoding.UTF8.GetByteCount(roomPlaylist)} bytes";
                _log.LogInfo($"[Competition] set_room_playlist received — {roomPlaylistSize} command_id={commandId}");
                // Not staleable: only the latest state matters, and writing it is cheap and
                // idempotent, so one picked up after a hang is still worth writing.
                RunOnMainThread(() =>
                {
                    if (RefuseAsGuest("set_room_playlist", commandId)) return;
                    try
                    {
                        if (RoomPlaylist.TryWrite(roomPlaylist, out var error))
                        {
                            EmitCommandAck(commandId, "ok");
                        }
                        else
                        {
                            _log.LogWarning($"[Competition] set_room_playlist not stored: {error}");
                            EmitCommandAck(commandId, "error", error);
                        }
                    }
                    catch (Exception ex) { _log.LogWarning($"[Competition] set_room_playlist failed: {ex.GetType().Name}: {ex.Message}"); EmitCommandAck(commandId, "error", ex.Message); }
                });
                break;

            case "request_catalog":
                _log.LogInfo("[Competition] request_catalog received — scheduling on main thread.");
                RunOnMainThread(() =>
                {
                    try
                    {
                        if (_onCatalogReady == null)
                        {
                            _log.LogWarning("[Competition] request_catalog: no catalog callback set.");
                            EmitCommandAck(commandId, "error", "no catalog callback");
                        }
                        else if (_trackControl.ExternalTryCatalogSnapshot(out var catalog))
                        {
                            catalog.TryGetValue("environments", out var envList);
                            _log.LogInfo($"[Competition] Catalog captured: {(envList as System.Collections.IList)?.Count ?? 0} environments.");
                            _onCatalogReady(catalog);
                            EmitCommandAck(commandId, "ok");
                        }
                        else
                        {
                            _log.LogWarning("[Competition] request_catalog: popup not available — open the track selection popup in-game first.");
                            EmitCommandAck(commandId, "error", "popup not available");
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning($"[Competition] request_catalog failed: {ex.GetType().Name}: {ex.Message}");
                        EmitCommandAck(commandId, "error", ex.Message);
                    }
                });
                break;

            case "send_chat":
                fields.TryGetValue("message", out var chatMessage);
                if (!string.IsNullOrWhiteSpace(chatMessage))
                {
                    var msg = chatMessage!;
                    // A chat send runs on the Unity main thread just like set_track does,
                    // so it is timed the same way: the server subtracts timing_queue_ms
                    // from timing_total_ms to see how long the game was actually blocked.
                    var chatTiming = new CommandTimingContext(commandId, "send_chat");
                    _log.LogInfo($"[Competition] send_chat received: \"{msg}\"");
                    ScheduleStaleable("send_chat", commandId, () =>
                    {
                        chatTiming.MarkMainThreadPickup();
                        if (RefuseAsGuest("send_chat", commandId)) return;
                        chatTiming.StartPhase("send");
                        try { _trackControl.ExternalSendChat(msg, "competition-server"); chatTiming.EndCurrentPhase(); EmitCommandAck(commandId, "ok", "", chatTiming); }
                        catch (Exception ex) { chatTiming.EndCurrentPhase(); _log.LogWarning($"[Competition] send_chat failed: {ex.GetType().Name}: {ex.Message}"); EmitCommandAck(commandId, "error", ex.Message, chatTiming); }
                    });
                }
                break;

            case "kick_player":
                if (fields.TryGetValue("actor", out var actorStr) && int.TryParse(actorStr, out var actorNum))
                {
                    _log.LogInfo($"[Competition] kick_player received — actor={actorNum}");
                    var a = actorNum;
                    RunOnMainThread(() =>
                    {
                        if (RefuseAsGuest("kick_player", commandId)) return;
                        try { _onKickPlayer?.Invoke(a); EmitCommandAck(commandId, "ok"); }
                        catch (Exception ex) { _log.LogWarning($"[Competition] kick_player failed: {ex.GetType().Name}: {ex.Message}"); EmitCommandAck(commandId, "error", ex.Message); }
                    });
                }
                break;

            case "create_game":
                _log.LogInfo("[Competition] create_game received — scheduling on main thread.");
                ScheduleStaleable("create_game", commandId, () =>
                {
                    try
                    {
                        _trackControl.ExternalCreateGame("competition-server");
                        EmitCommandAck(commandId, "ok");
                    }
                    catch (Exception ex) { _log.LogWarning($"[Competition] create_game failed: {ex.GetType().Name}: {ex.Message}"); EmitCommandAck(commandId, "error", ex.Message); }
                });
                break;

            case "leave_lobby":
                _log.LogInfo("[Competition] leave_lobby received — scheduling on main thread.");
                RunOnMainThread(() =>
                {
                    try
                    {
                        PhotonNetwork.LeaveRoom();
                        EmitCommandAck(commandId, "ok");
                    }
                    catch (Exception ex) { _log.LogWarning($"[Competition] leave_lobby failed: {ex.GetType().Name}: {ex.Message}"); EmitCommandAck(commandId, "error", ex.Message); }
                });
                break;

            case "disconnect":
                _log.LogInfo("[Competition] disconnect received — scheduling on main thread.");
                RunOnMainThread(() =>
                {
                    try
                    {
                        PhotonNetwork.Disconnect();
                        EmitCommandAck(commandId, "ok");
                    }
                    catch (Exception ex) { _log.LogWarning($"[Competition] disconnect failed: {ex.GetType().Name}: {ex.Message}"); EmitCommandAck(commandId, "error", ex.Message); }
                });
                break;

            case "request_lobby_status":
                _log.LogInfo("[Competition] request_lobby_status received — scheduling on main thread.");
                ScheduleStaleable("request_lobby_status", commandId, () =>
                {
                    try
                    {
                        _onLobbyStatusRequested?.Invoke();
                        EmitCommandAck(commandId, "ok");
                    }
                    catch (Exception ex) { _log.LogWarning($"[Competition] request_lobby_status failed: {ex.GetType().Name}: {ex.Message}"); EmitCommandAck(commandId, "error", ex.Message); }
                });
                break;

            case "navigate_to_lobby":
                _log.LogInfo("[Competition] navigate_to_lobby received — scheduling on main thread.");
                ScheduleStaleable("navigate_to_lobby", commandId, () =>
                {
                    try
                    {
                        _trackControl.ExternalNavigateToLobby("competition-server");
                        EmitCommandAck(commandId, "ok");
                    }
                    catch (Exception ex) { _log.LogWarning($"[Competition] navigate_to_lobby failed: {ex.GetType().Name}: {ex.Message}"); EmitCommandAck(commandId, "error", ex.Message); }
                });
                break;

            case "request_stats":
                _log.LogInfo("[Competition] request_stats received.");
                EmitCommandAck(commandId, "ok");
                break;

            default:
                _log.LogWarning($"[Competition] Unknown command: {cmd}");
                Stats.LastCommand = $"unknown:{cmd}";
                break;
        }
    }
}
