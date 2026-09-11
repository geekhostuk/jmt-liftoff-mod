using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using JmtLiftoffMod.Features.Chat;
using JmtLiftoffMod.Features.Competition;
using JmtLiftoffMod.Features.Diagnostics;
using JmtLiftoffMod.Features.Lobby;
using JmtLiftoffMod.Features.MultiplayerTrackControl;
using JmtLiftoffMod.Features.Racing;

namespace JmtLiftoffMod;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin, IOnEventCallback, IInRoomCallbacks, IConnectionCallbacks
{
    public const string PluginGuid = "uk.co.geekhost.jmtliftoffmod";
    public const string PluginName = "JMT Liftoff Mod";
    public const string PluginVersion = BuildInfo.Version;
    public const string BuildMarker = BuildInfo.Marker;

    private ManualLogSource _log = null!;
    private string _filePath = null!;
    private string _stateFilePath = null!;
    private string _raceFilePath = null!;
    private string _raceJsonFilePath = null!;
    private string _eventCodeDir = null!;
    private string _pluginDir = null!;
    private string _sessionId = string.Empty;
    private string _raceId = string.Empty;
    private int _raceOrdinal;
    private long _raceEventOrdinal;
    private long _chatOrdinal;
    private bool _registered;
    private bool _isQuitting;
    private bool _photonWasDisconnected;
    private bool _raceEndEmitted;
    private const int MaxDescribeDepth = 6;
    private const int MaxCollectionItems = 40;
    private const int MaxBytePreview = 64;
    private const int ClassicRaceLapCount = 3;
    private static readonly string LapTimesSuffix = "_laptimes";
    private static readonly Regex GmsLapArrayRegex = new(@"Single\[\]\[(?<count>\d+)\]\s\[(?<vals>[^\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex GmsLapValueRegex = new(@"float\s(?<v>-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex GmsCheckpointRegex = new(@"RacePlayerCheckpointInfo\s\{ID=string\s""(?<id>[^""]+)"",\sLap=int\s(?<lap>\d+),\sTime=float\s(?<time>-?\d+(?:\.\d+)?)\}", RegexOptions.Compiled);
    private readonly Dictionary<int, string> _actorToNick = new();
    private readonly Dictionary<int, string> _actorToUserId = new();
    private readonly Dictionary<int, int> _actorToRaceState = new();
    private readonly HashSet<int> _raceParticipants = new();
    private readonly Dictionary<int, PilotLapState> _actorLapState = new();
    private readonly Dictionary<string, PilotLapState> _guidLapState = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _actorLastCheckpointId = new();
    private MultiplayerTrackControlService? _multiplayerTrackControl;
    private CompetitionClient? _competitionClient;
    private JmtLiftoffMod.Features.Chat.ChatCaptureService? _chatCapture;
    private CheckpointHookService? _checkpointHook;
    private LobbyStatusService? _lobbyStatus;
    private BotStatsOverlay? _botStatsOverlay;
    // In-memory config for all the internal/diagnostic settings that should not appear in the
    // user-facing .cfg. Only the essential Competition connection settings are bound to the
    // real plugin Config.
    private ConfigFile _hiddenConfig = null!;
    private ConfigEntry<bool> _enableCheckpointHook = null!;
    private ConfigEntry<bool> _emitLobbyStatusOnChange = null!;
    private ConfigEntry<bool> _showBotStatsOverlay = null!;
    private ConfigEntry<KeyboardShortcut> _statsOverlayHotkey = null!;
    private ConfigEntry<int> _minLapMs = null!;
    private ConfigEntry<int> _trackChangeGraceSecs = null!;
    private ConfigEntry<int> _maxLapsPerRace = null!;
    private ConfigEntry<int> _cfgDisconnectTimeoutMs = null!;
    private ConfigEntry<int> _cfgSentCountAllowance = null!;
    private ConfigEntry<int> _cfgTimePingIntervalMs = null!;
    private ConfigEntry<bool> _cfgEnableEventsLog = null!;
    private ConfigEntry<bool> _cfgEnablePerCodeLogs = null!;
    private ConfigEntry<bool> _cfgEnableStateLog = null!;
    private ConfigEntry<bool> _cfgEnableRaceJsonl = null!;
    private ConfigEntry<bool> _cfgEvent200DeepDump = null!;
    private ConfigEntry<int> _cfgLogRetentionDays = null!;
    private ConfigEntry<string> _cfgSilentCodesCsv = null!;
    private HashSet<byte> _silentCodes = new() { 201, 226 };
    private bool _photonTuningApplied;
    private int _photonTuningLastSignature;
    private DateTime _suppressEvent200Until = DateTime.MinValue;
    // The laps of each pilot's current run, as GMS last reported them. A run is everything
    // since the pilot's last respawn: GMS grows it one lap at a time and a respawn empties it.
    private readonly Dictionary<int, List<int>> _actorGmsRun = new();
    // When each pilot last respawned, and last finished a lap, in the current race. Between
    // them they say how long the attempt a reset abandons had been running.
    private readonly Dictionary<int, DateTime> _actorSpawnUtc = new();
    private readonly Dictionary<int, DateTime> _actorLastLapUtc = new();
    // True only until the first StartNewRace() call. Controls whether the GMS
    // baseline-detection heuristic runs. At plugin startup it prevents pre-session
    // laps from being recorded; after any race reset the game resets GMS data so
    // the heuristic is no longer needed and would cause series mismatches.
    private bool _needGmsBaseline = true;

    // Background log writer — drains queued file writes off the main thread
    private readonly BlockingCollection<(string path, string text)> _logQueue = new(1000);
    private System.Threading.Thread? _logWriterThread;

    // Main-thread liveness tracker. Updated on every Update() tick (main thread) and
    // read from the watchdog + the competition keepalive (background threads). Stored
    // as Unix ms; 0 means "no tick yet".
    private long _lastMainThreadTickUtcMs;
    private long _maxMainThreadGapMs;
    private System.Threading.Thread? _watchdogThread;

    /// <summary>Exposed so the competition keepalive thread can enrich its payload with live main-thread health.</summary>
    internal long GetMainThreadLastTickUtcMs() => System.Threading.Volatile.Read(ref _lastMainThreadTickUtcMs);

    // Cached hostInactivityMinutes field lookup (avoids scanning all assemblies every call)
    private Type? _hostInactivityType;
    private FieldInfo? _hostInactivityField;
    private bool _hostInactivityScanned;

    /// <summary>
    /// Finds the lobby controller's hostInactivityMinutes field and sets it to a very high value
    /// to prevent Liftoff's built-in inactivity kick when alone in the lobby.
    /// Re-applies every keepalive tick so new controller instances (after scene/track changes) are patched.
    /// The assembly/type scan runs once and is cached for subsequent calls.
    /// </summary>
    private void DisableLiftoffInactivityKick()
    {
        // One-time discovery scan — find the type and field once
        if (!_hostInactivityScanned)
        {
            _hostInactivityScanned = true;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                Type[] types;
                try { types = assembly.GetTypes(); } catch { continue; }

                foreach (var type in types)
                {
                    var field = type.GetField("hostInactivityMinutes",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field != null && field.FieldType == typeof(int))
                    {
                        _hostInactivityType = type;
                        _hostInactivityField = field;
                        _log.LogInfo($"[Keepalive] Discovered hostInactivityMinutes on {type.FullName}");
                        break;
                    }
                }
                if (_hostInactivityField != null) break;
            }
            if (_hostInactivityField == null)
            {
                _log.LogWarning("[Keepalive] hostInactivityMinutes field not found in any loaded assembly.");
                return;
            }
        }

        if (_hostInactivityField == null || _hostInactivityType == null) return;

        // Fast path: just patch live instances using cached type/field
        var liveObjects = Resources.FindObjectsOfTypeAll(_hostInactivityType);
        foreach (var obj in liveObjects)
        {
            var currentValue = (int)_hostInactivityField.GetValue(obj);
            if (currentValue < 999999)
            {
                _hostInactivityField.SetValue(obj, 999999);
                _log.LogInfo($"[Keepalive] Set hostInactivityMinutes from {currentValue} to 999999 on {obj.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// Applies DisconnectTimeout / SentCountAllowance / TimePingInterval from config to the
    /// live Photon peer. Safe to call repeatedly — the peer is persistent on Photon reconnects
    /// but the settings can be reset by the game's own connection setup, so we re-apply after
    /// each OnConnected/OnConnectedToMaster callback. Logs only on initial apply or when
    /// config values change.
    /// </summary>
    private void ApplyPhotonNetworkTuning()
    {
        var peer = PhotonNetwork.NetworkingClient?.LoadBalancingPeer;
        if (peer == null) return;

        var disconnectMs = _cfgDisconnectTimeoutMs?.Value ?? 30000;
        var sentCount = _cfgSentCountAllowance?.Value ?? 7;
        var pingMs = _cfgTimePingIntervalMs?.Value ?? 1000;

        try { peer.DisconnectTimeout = disconnectMs; }
        catch (Exception ex) { _log.LogWarning($"[Photon] Could not set DisconnectTimeout: {ex.Message}"); }

        try { peer.SentCountAllowance = sentCount; }
        catch (Exception ex) { _log.LogWarning($"[Photon] Could not set SentCountAllowance: {ex.Message}"); }

        try { peer.TimePingInterval = pingMs; }
        catch (Exception ex) { _log.LogWarning($"[Photon] Could not set TimePingInterval: {ex.Message}"); }

        var signature = unchecked((disconnectMs * 397) ^ (sentCount * 31) ^ pingMs);
        if (!_photonTuningApplied || signature != _photonTuningLastSignature)
        {
            _photonTuningApplied = true;
            _photonTuningLastSignature = signature;
            _log.LogInfo($"[Photon] Tuning applied disconnectTimeoutMs={disconnectMs} sentCountAllowance={sentCount} pingIntervalMs={pingMs}");
        }
    }

    private static HashSet<byte> ParseSilentCodes(string csv)
    {
        var set = new HashSet<byte>();
        if (string.IsNullOrWhiteSpace(csv)) return set;
        foreach (var part in csv.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            if (byte.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                set.Add(code);
        }
        return set;
    }

    private static string DescribeStatic(object? obj, int depth)
    {
        if (obj == null) return "null";
        if (depth > 3) return obj.GetType().Name;
        if (obj is string s) return $"\"{s}\"";
        if (obj is byte[] ba) return $"byte[{ba.Length}]={BitConverter.ToString(ba.Take(16).ToArray())}";
        if (obj is PhotonHashtable ht)
        {
            var pairs = ht.Cast<System.Collections.DictionaryEntry>()
                .Select(e => $"{DescribeStatic(e.Key, depth+1)}={DescribeStatic(e.Value, depth+1)}");
            return "{" + string.Join(", ", pairs) + "}";
        }
        if (obj is object[] arr)
        {
            var items = arr.Take(8).Select(x => DescribeStatic(x, depth+1));
            return $"[{string.Join(", ", items)}]";
        }
        return $"{obj.GetType().Name}:{obj}";
    }

    private sealed class PilotLapState
    {
        public int Actor;
        public string Nick = "Unknown";
        public string Guid = string.Empty;
        public readonly List<int> LapTimesMs = new();
        public bool IsComplete;
        public string CompletionReason = string.Empty;
    }

    protected void Awake()
    {
        _log = Logger;
        DontDestroyOnLoad(gameObject);
        gameObject.hideFlags = HideFlags.HideAndDontSave;

        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll();

        // All settings below are internal tuning and bound to an in-memory config so they never
        // appear in the user-facing .cfg — the visible options are the Competition connection
        // settings.
        _hiddenConfig = HiddenConfig.Create();

        // ── Network tuning ──
        _cfgDisconnectTimeoutMs = _hiddenConfig.Bind(
            "Network", "DisconnectTimeoutMs", 30000,
            new ConfigDescription(
                "Photon peer disconnect grace in milliseconds. Default Photon value is 10000; " +
                "raise to 30000 when running on a CPU-rendered (WARP) ESXi VM where the main " +
                "thread can stall longer than 10s under a full lobby.",
                new AcceptableValueRange<int>(5000, 120000)));

        _cfgSentCountAllowance = _hiddenConfig.Bind(
            "Network", "SentCountAllowance", 7,
            new ConfigDescription(
                "Photon retry count before giving up on unacknowledged commands. Default is 5; " +
                "slightly higher tolerates short packet loss without hiding real network failure.",
                new AcceptableValueRange<int>(3, 20)));

        _cfgTimePingIntervalMs = _hiddenConfig.Bind(
            "Network", "TimePingIntervalMs", 1000,
            new ConfigDescription(
                "Photon ping interval in milliseconds. Shorter keeps the connection livelier; " +
                "default is 1000.",
                new AcceptableValueRange<int>(250, 5000)));

        // ── Logging toggles ──────────────────────────────────────────────
        _cfgEnableEventsLog = _hiddenConfig.Bind(
            "Logging", "EnableEventsLog", false,
            "Write the raw photon-events-*.log firehose (every Photon event with full reflection dump). " +
            "Disabled by default — very high allocation volume, only enable to reproduce an issue.");

        _cfgEnablePerCodeLogs = _hiddenConfig.Bind(
            "Logging", "EnablePerCodeLogs", false,
            "Write one log file per Photon event code under event-codes/. Disabled by default — " +
            "even more expensive than EnableEventsLog because each event is serialised twice.");

        _cfgEnableStateLog = _hiddenConfig.Bind(
            "Logging", "EnableStateLog", false,
            "Write photon-state-*.log (room/player/master-client callbacks). Off by default for host " +
            "mode: building each line runs a recursive reflection dump (Describe) of Photon property " +
            "payloads on the main thread, which causes in-game stutter during a race. Enable only to " +
            "reproduce a lobby/room issue. The competition server uses the race JSONL, not this file.");

        _cfgEnableRaceJsonl = _hiddenConfig.Bind(
            "Logging", "EnableRaceJsonl", true,
            "Write photon-race-*.jsonl with structured race events. Required for the competition server; " +
            "only disable for fully offline testing.");

        _cfgEvent200DeepDump = _hiddenConfig.Bind(
            "Logging", "Event200DeepDump", false,
            "Emit the full reflection dump for Event 200 (DroneConfiguration / Sprite / Texture2D / GMS). " +
            "This is the plugin's single biggest allocation path — keep off in production.");

        _cfgLogRetentionDays = _hiddenConfig.Bind(
            "Logging", "LogRetentionDays", 7,
            new ConfigDescription(
                "Age in days before plugin log files are deleted on startup.",
                new AcceptableValueRange<int>(1, 90)));

        _cfgSilentCodesCsv = _hiddenConfig.Bind(
            "Logging", "SilentCodes", "201,226",
            "Comma-separated Photon event codes to skip in file logging entirely (high-frequency " +
            "telemetry with no operational value). Default matches the previous hard-coded list.");

        _silentCodes = ParseSilentCodes(_cfgSilentCodesCsv.Value);

        _pluginDir = Path.Combine(Paths.BepInExRootPath, "plugins", "JmtLiftoffMod");
        Directory.CreateDirectory(_pluginDir);
        _eventCodeDir = Path.Combine(_pluginDir, "event-codes");
        Directory.CreateDirectory(_eventCodeDir);
        CleanOldLogs(_pluginDir, _eventCodeDir, maxAgeDays: _cfgLogRetentionDays.Value);

        _filePath = Path.Combine(_pluginDir, $"photon-events-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        _stateFilePath = Path.Combine(_pluginDir, $"photon-state-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        _raceFilePath = Path.Combine(_pluginDir, $"photon-race-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        _raceJsonFilePath = Path.Combine(_pluginDir, $"photon-race-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
        _sessionId = Guid.NewGuid().ToString("N");
        _raceId = CreateRaceId();
        _raceOrdinal = 1;
        _raceEventOrdinal = 0;
        if (_cfgEnableEventsLog.Value)
            File.WriteAllText(_filePath, $"[{DateTime.UtcNow:O}] {PluginName} session started{Environment.NewLine}");
        if (_cfgEnableStateLog.Value)
            File.WriteAllText(_stateFilePath, $"[{DateTime.UtcNow:O}] {PluginName} state session started{Environment.NewLine}");
        File.WriteAllText(_raceFilePath, $"[{DateTime.UtcNow:O}] {PluginName} race session started{Environment.NewLine}");
        if (_cfgEnableRaceJsonl.Value)
            File.WriteAllText(_raceJsonFilePath, string.Empty);

        // Start background log writer thread so file I/O doesn't block the main thread
        _logWriterThread = new System.Threading.Thread(() =>
        {
            foreach (var (path, text) in _logQueue.GetConsumingEnumerable())
            {
                try { File.AppendAllText(path, text + Environment.NewLine); }
                catch { /* best effort */ }
            }
        }) { IsBackground = true, Name = "LiftoffLogWriter" };
        _logWriterThread.Start();

        // Main-thread watchdog — logs when Update() hasn't run recently. Runs on its
        // own thread so it can still emit warnings while Unity is frozen. Purely
        // diagnostic: it can't unblock the main thread, only surface the problem.
        _watchdogThread = new System.Threading.Thread(() =>
        {
            while (!_isQuitting)
            {
                System.Threading.Thread.Sleep(2000);
                try
                {
                    var last = System.Threading.Volatile.Read(ref _lastMainThreadTickUtcMs);
                    if (last == 0) continue; // main thread hasn't ticked yet
                    var gap = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last;
                    if (gap > _maxMainThreadGapMs)
                        System.Threading.Volatile.Write(ref _maxMainThreadGapMs, gap);
                    if (gap > 5000)
                        _log.LogWarning($"[Watchdog] Main thread gap {gap} ms (max seen this session: {_maxMainThreadGapMs} ms)");
                }
                catch { /* watchdog must never throw */ }
            }
        }) { IsBackground = true, Name = "LiftoffMainThreadWatchdog" };
        _watchdogThread.Start();

        _log.LogInfo($"{PluginName} loaded. Log file: {_filePath}");
        _log.LogInfo($"{PluginName} build marker: {BuildMarker}");
        _log.LogInfo(
            $"[Logging] events={_cfgEnableEventsLog.Value} perCode={_cfgEnablePerCodeLogs.Value} " +
            $"state={_cfgEnableStateLog.Value} raceJsonl={_cfgEnableRaceJsonl.Value} " +
            $"event200Deep={_cfgEvent200DeepDump.Value} retentionDays={_cfgLogRetentionDays.Value} " +
            $"silentCodes=[{string.Join(",", _silentCodes)}]");
        _log.LogInfo(
            $"[Network] disconnectTimeoutMs={_cfgDisconnectTimeoutMs.Value} " +
            $"sentCountAllowance={_cfgSentCountAllowance.Value} " +
            $"timePingIntervalMs={_cfgTimePingIntervalMs.Value}");
        AppendStateLine($"BUILD marker={BuildMarker} version={PluginVersion}");
        AppendRaceEvent("session_started", new Dictionary<string, object?>
        {
            ["plugin"] = PluginName,
            ["version"] = PluginVersion,
            ["buildMarker"] = BuildMarker
        });

        _multiplayerTrackControl = new MultiplayerTrackControlService(
            this,
            _pluginDir,
            Logger,
            AppendStateLine,
            Describe,
            stateLogEnabled: () => StateLogEnabled);
        _multiplayerTrackControl.Initialize();

        _minLapMs = _hiddenConfig.Bind(
            "Recording", "MinLapMs", 0,
            "Hard minimum lap time in milliseconds. Laps shorter than this are always discarded. " +
            "Set to 0 (default) to rely on smarter detection instead of a fixed floor.");

        _trackChangeGraceSecs = _hiddenConfig.Bind(
            "Recording", "TrackChangeGraceSeconds", 10,
            "Seconds after a track change command during which lap events are suppressed. " +
            "When the admin changes track, pilots mid-lap receive a partial-lap event from the game. " +
            "This window discards those bogus times. Increase if you see partial laps slipping through.");

        _maxLapsPerRace = _hiddenConfig.Bind(
            "Recording", "MaxLapsPerRace", 0,
            "Maximum laps per pilot per race before they are marked complete and further laps ignored. " +
            "Set to 0 (default) for InfiniteRace mode where laps accumulate without limit. " +
            "Set to 3 for ClassicRace mode.");

        _gameKeepaliveIntervalSecs = _hiddenConfig.Bind(
            "Recording", "GameKeepaliveIntervalSecs", 120,
            "Seconds between game-level keep-alive signals. Prevents Liftoff's own host inactivity kick " +
            "when sitting in the lobby waiting room. Set to 0 to disable.");

        var competitionConfig = new CompetitionConfig(Config);
        var unitySyncContext = System.Threading.SynchronizationContext.Current;
        _competitionClient = new CompetitionClient(
            competitionConfig, Logger, _multiplayerTrackControl, unitySyncContext,
            getLocalActor: () => PhotonNetwork.LocalPlayer?.ActorNumber,
            sessionId: _sessionId,
            onCatalogReady: catalogData => AppendRaceEvent("track_catalog", catalogData),
            onKickPlayer: actor => TryKickPlayer(actor),
            onTrackChanging: () =>
            {
                var graceSecs = _trackChangeGraceSecs.Value;
                _suppressEvent200Until = DateTime.UtcNow.AddSeconds(graceSecs);
                _log.LogInfo($"[Recording] Track change — suppressing lap events for {graceSecs}s");
                StartNewRace("track_change");
                EmitPlayerList();
                try { DisableLiftoffInactivityKick(); } catch { /* best effort */ }
            },
            onLobbyStatusRequested: () => _lobbyStatus?.EmitLobbyStatus(),
            onConnected: () =>
            {
                _competitionClient?.EnqueueEvent(
                    SerializeJsonObject(SessionStartedPayload())
                );
                EmitPlayerList();
            },
            getMainThreadLastTickUtcMs: () => GetMainThreadLastTickUtcMs());
        _competitionClient.Start();

        _chatCapture = new ChatCaptureService(Logger, PluginGuid, (userId, userName, message) =>
        {
            // Resolve actor from known player data (userId = Steam ID)
            int? actor = null;
            foreach (var kv in _actorToUserId)
            {
                if (string.Equals(kv.Value, userId, StringComparison.OrdinalIgnoreCase))
                {
                    actor = kv.Key;
                    break;
                }
            }

            // Messages the bot sent itself come back through the same render path as anyone
            // else's. Flagging them stops a server acting on its own announcements.
            var localUserId = PhotonNetwork.LocalPlayer?.UserId;
            var self = !string.IsNullOrEmpty(localUserId)
                    && string.Equals(localUserId, userId, StringComparison.OrdinalIgnoreCase);

            AppendRaceEvent("chat_message", new Dictionary<string, object?>
            {
                ["actor"]   = actor,
                ["user_id"] = userId,
                ["nick"]    = userName,
                ["message"] = message,
                // Monotonic for the whole plugin session and never reset, so (session_id,
                // chat_id) is a stable dedupe key and messages stay orderable across races —
                // event_ordinal restarts at every race and cannot do that.
                ["chat_id"] = ++_chatOrdinal,
                ["self"]    = self
            });
        });
        _chatCapture.Install();

        // Re-send session_started now that the client exists so the server receives it and can
        // create the session row before any race events. Deliberately after chat capture is
        // installed, so chat_capture_mode reports the mode actually in force.
        _competitionClient.EnqueueEvent(
            SerializeJsonObject(SessionStartedPayload())
        );

        // ── Lobby status service ──────────────────────────────────────────
        _emitLobbyStatusOnChange = _hiddenConfig.Bind(
            "Lobby", "EmitLobbyStatusOnChange", true,
            "Automatically emit lobby_status events when players join/leave or host changes.");

        _lobbyStatus = new LobbyStatusService(Logger, AppendRaceEvent,
            getScreenState: () => _multiplayerTrackControl?.GetScreenState());

        // ── Checkpoint/gate timing ────────────────────────────────────────
        _enableCheckpointHook = _hiddenConfig.Bind(
            "Racing", "EnableCheckpointHook", true,
            "Enable Harmony patch on RaceCheckpoint.Trigger() for gate-by-gate timing. " +
            "Disable if it causes issues with a new game version.");

        if (_enableCheckpointHook.Value)
        {
            _checkpointHook = new CheckpointHookService(Logger, PluginGuid);
            _checkpointHook.SetCallback(gateData =>
            {
                gateData.Nick = ResolveNick(gateData.Actor);
                AppendRaceEvent("gate_passed", new Dictionary<string, object?>
                {
                    ["actor"] = gateData.Actor,
                    ["nick"] = gateData.Nick,
                    ["checkpoint_id"] = gateData.CheckpointId,
                    ["trigger_id"] = gateData.TriggerId,
                    ["gate_time_sec"] = gateData.GameTimeSec,
                });

                // Try to compute sector split
                var split = _checkpointHook?.TryComputeSectorSplit(gateData.Actor, gateData.Nick);
                if (split != null)
                {
                    AppendRaceEvent("sector_split", new Dictionary<string, object?>
                    {
                        ["actor"] = split.Actor,
                        ["nick"] = split.Nick,
                        ["sector_index"] = split.SectorIndex,
                        ["from_gate"] = split.FromGate,
                        ["to_gate"] = split.ToGate,
                        ["sector_ms"] = split.SectorMs,
                        ["gate_time_sec"] = split.GameTimeSec,
                    });
                }
            });

            if (!_checkpointHook.TryInstall())
                _log.LogWarning("[Plugin] Checkpoint hook not installed — gate timing unavailable. GMS-only checkpoint data will still be captured.");
        }

        // ── Bot stats overlay ─────────────────────────────────────────────
        _showBotStatsOverlay = _hiddenConfig.Bind(
            "Diagnostics", "ShowBotStatsOverlay", false,
            "Show the in-game bot stats overlay on startup.");

        _statsOverlayHotkey = _hiddenConfig.Bind(
            "Diagnostics", "StatsOverlayHotkey",
            new KeyboardShortcut(KeyCode.F5, KeyCode.LeftControl),
            "Hotkey to toggle the bot stats overlay.");

        if (_competitionClient != null)
        {
            _botStatsOverlay = new BotStatsOverlay(
                Logger,
                _competitionClient.Stats,
                _showBotStatsOverlay,
                _statsOverlayHotkey,
                getRaceId: () => _raceId,
                getRaceOrdinal: () => _raceOrdinal,
                getParticipantCount: () => _raceParticipants.Count);
        }

        // Push initial race context to competition client
        _competitionClient?.SetRaceContext(_raceId, _raceOrdinal);
    }


    protected void OnEnable()
    {
        // Try immediately (in case Photon is already initialised)
        TryRegister();
    }

    private int _updateCount;
    private float _nextGameKeepalive;
    private ConfigEntry<int> _gameKeepaliveIntervalSecs = null!;

    protected void Update()
    {
        // Watchdog liveness beacon — must be first so any blocking work below is
        // visible as a gap from a background thread's perspective.
        System.Threading.Volatile.Write(
            ref _lastMainThreadTickUtcMs,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        _updateCount++;

        // Keep trying until it works – Liftoff may initialise Photon later
        if (!_registered)
            TryRegister();

        _multiplayerTrackControl?.Update();
        _competitionClient?.Update();
        _botStatsOverlay?.Update();

        // After a Photon disconnect+reconnect, send a fresh player list so the
        // server drops stale players from the old lobby.
        if (_photonWasDisconnected && PhotonNetwork.InRoom)
        {
            _photonWasDisconnected = false;
            _log.LogInfo("[Photon] Rejoined room after disconnect — emitting fresh player list");
            EmitPlayerList();
        }

        // Game-level keep-alive: prevent Liftoff's own host inactivity kick
        // by overriding the hostInactivityMinutes field on the lobby controller.
        if (PhotonNetwork.InRoom && Time.time >= _nextGameKeepalive)
        {
            var interval = _gameKeepaliveIntervalSecs?.Value ?? 120;
            if (interval > 0)
            {
                _nextGameKeepalive = Time.time + interval;
                try
                {
                    DisableLiftoffInactivityKick();
                }
                catch { /* best effort */ }
            }
        }
    }

    private void OnDisable()
    {
        // Keep callback active through scene/enable state changes.
    }

    protected void OnGUI()
    {
        _multiplayerTrackControl?.OnGUI();
        _botStatsOverlay?.OnGUI();
    }

    protected void OnDestroy()
    {
        if (_isQuitting)
        {
            DisposeServices();
            TryUnregister();
            return;
        }

        _log.LogWarning("Plugin OnDestroy fired before OnApplicationQuit. Callback unregistration skipped.");
    }

    protected void OnApplicationQuit()
    {
        _isQuitting = true;
        DisposeServices();
        TryUnregister();

        // Drain remaining log writes and shut down the background writer
        _logQueue.CompleteAdding();
        _logWriterThread?.Join(TimeSpan.FromSeconds(2));
    }

    private void DisposeServices()
    {
        _chatCapture?.Dispose();
        _chatCapture = null;
        _checkpointHook?.Dispose();
        _checkpointHook = null;
        _competitionClient?.Dispose();
        _competitionClient = null;
        _multiplayerTrackControl?.Dispose();
        _multiplayerTrackControl = null;
        _lobbyStatus = null;
        _botStatsOverlay = null;
    }

    private void TryRegister()
    {
        if (_registered) return;

        try
        {
            PhotonNetwork.AddCallbackTarget(this);
            _registered = true;
            _log.LogInfo("Registered as Photon callback target (IOnEventCallback).");
            // If Photon already finished connecting before we registered, the connection
            // callbacks won't fire again — apply network tuning now so DisconnectTimeout
            // is in place from the first race.
            try { ApplyPhotonNetworkTuning(); } catch { /* best effort */ }
            EmitPlayerList();
        }
        catch
        {
            // Photon not ready yet; ignore and retry next frame
        }
    }

    private void TryKickPlayer(int actor)
    {
        try
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                _log.LogWarning($"[Plugin] Cannot kick Actor={actor}: not master client");
                AppendRaceEvent("kick_result", new Dictionary<string, object?>
                {
                    ["actor"] = actor,
                    ["success"] = false,
                    ["reason"] = "not_master_client"
                });
                return;
            }

            var player = PhotonNetwork.CurrentRoom?.GetPlayer(actor);
            if (player == null)
            {
                _log.LogWarning($"[Plugin] Cannot kick Actor={actor}: player not found in room");
                AppendRaceEvent("kick_result", new Dictionary<string, object?>
                {
                    ["actor"] = actor,
                    ["success"] = false,
                    ["reason"] = "player_not_found"
                });
                return;
            }

            var nick = player.NickName;

            if (string.IsNullOrEmpty(nick))
            {
                _log.LogWarning($"[Plugin] Cannot kick Actor={actor}: NickName is empty");
                AppendRaceEvent("kick_result", new Dictionary<string, object?>
                {
                    ["actor"] = actor,
                    ["success"] = false,
                    ["reason"] = "no_nick_name"
                });
                return;
            }

            // Use game's own RPCKicked mechanism, found via reflection.
            _log.LogInfo($"[Plugin] Pre-kick state: NetworkClientState={PhotonNetwork.NetworkClientState} IsMasterClient={PhotonNetwork.IsMasterClient} InRoom={PhotonNetwork.InRoom}");
            var kicked = TryKickViaGameRpc(player);

            AppendRaceEvent("kick_result", new Dictionary<string, object?>
            {
                ["actor"] = actor,
                ["nick"] = nick,
                ["success"] = kicked,
                ["reason"] = kicked ? (object?)null : "game_rpc_failed"
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Plugin] Kick failed for Actor={actor}: {ex.Message}");
            AppendRaceEvent("kick_result", new Dictionary<string, object?>
            {
                ["actor"] = actor,
                ["success"] = false,
                ["reason"] = ex.Message
            });
        }
    }

    /// <summary>
    /// Calls Liftoff's own RPCKicked Photon RPC on the target player's PhotonView.
    /// The method and its parameter types are discovered at runtime via reflection so we
    /// don't need to hardcode obfuscated class names.
    /// </summary>
    private bool TryKickViaGameRpc(Player target)
    {
        try
        {
            // Find the RPCKicked method in Assembly-CSharp
            var gameDll = System.AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (gameDll == null)
            {
                _log.LogWarning("[Plugin] TryKickViaGameRpc: Assembly-CSharp not found");
                return false;
            }

            System.Reflection.MethodInfo? rpcMethod = null;
            System.Type? rpcType = null;
            foreach (var type in gameDll.GetTypes())
            {
                var m = type.GetMethod("RPCKicked",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                if (m != null)
                {
                    rpcMethod = m;
                    rpcType = type;
                    break;
                }
            }

            if (rpcMethod == null || rpcType == null)
            {
                _log.LogWarning("[Plugin] TryKickViaGameRpc: RPCKicked method not found in Assembly-CSharp");
                return false;
            }


            // Find a PhotonView in the scene that has this MonoBehaviour
            PhotonView? kickPhotonView = null;
            foreach (var pv in PhotonNetwork.PhotonViewCollection)
            {
                if (pv.GetComponent(rpcType) != null)
                {
                    kickPhotonView = pv;
                    break;
                }
            }

            if (kickPhotonView == null)
            {
                _log.LogWarning($"[Plugin] TryKickViaGameRpc: No PhotonView found with component {rpcType.Name}");
                return false;
            }

            // Build RPC params: create default instances of each parameter type.
            // RPCKicked(param1, param2):
            //   param2 (index 1) = auth context: Player field must be LocalPlayer so IsMasterClient is true
            //   param1 (index 0) = kick data: Player field set to target, bool fields set true for kick path
            var paramInfos = rpcMethod.GetParameters();
            var rpcParams = new object?[paramInfos.Length];
            for (int i = 0; i < paramInfos.Length; i++)
            {
                var pt = paramInfos[i].ParameterType;
                try
                {
                    var instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(pt);
                    foreach (var field in pt.GetFields(
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic))
                    {
                        if (field.FieldType == typeof(bool))
                        {
                            try { field.SetValue(instance, true); } catch { }
                        }
                        else if (field.FieldType == typeof(Player))
                        {
                            // param[1] = auth context → local player (master client) so IsMasterClient passes
                            // param[0] = kick data → target player
                            var playerVal = (i == 1) ? PhotonNetwork.LocalPlayer : target;
                            try { field.SetValue(instance, playerVal); } catch { }
                        }
                    }
                    rpcParams[i] = instance;
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"[Plugin] TryKickViaGameRpc: Could not create param[{i}] ({pt.Name}): {ex.Message}");
                    rpcParams[i] = null;
                }
            }

            kickPhotonView.RPC("RPCKicked", target, rpcParams);
            _log.LogInfo($"[Plugin] TryKickViaGameRpc: RPC sent to Actor={target.ActorNumber}");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Plugin] TryKickViaGameRpc failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void EmitPlayerList()
    {
        try
        {
            if (!PhotonNetwork.InRoom) return;
            var players = PhotonNetwork.PlayerList;
            if (players == null || players.Length == 0) return;

            var playerData = players.Select(p => new Dictionary<string, object?>
            {
                ["actor"] = p.ActorNumber,
                ["nick"] = p.NickName,
                ["user_id"] = string.IsNullOrEmpty(p.UserId) ? (object?)null : p.UserId
            }).ToList<object?>();

            AppendRaceEvent("player_list", new Dictionary<string, object?>
            {
                ["players"] = playerData
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning($"EmitPlayerList failed: {ex.Message}");
        }
    }

    private void TryUnregister()
    {
        if (!_registered) return;

        try
        {
            PhotonNetwork.RemoveCallbackTarget(this);
        }
        catch
        {
            // ignore
        }

        _registered = false;
        _log.LogInfo("Unregistered Photon callback target.");
    }

    // High-frequency event codes that carry no competition value — skip file logging for these.
    // 201 = player position/movement telemetry (~10 Hz per player)
    // 226 = periodic room ping/heartbeat
    // The set above is replaced from the `Logging.SilentCodes` config at Awake; these are
    // only the pre-config defaults used until Awake runs.
    private static readonly HashSet<byte> _silentEventCodesDefault = new() { 201, 226 };

    // Movement-based activity detection from Event 201 position telemetry
    private const float MovementThreshold = 2f;
    private const float ActivityThrottleSecs = 5f;
    private readonly Dictionary<int, float[]> _lastKnownPosition = new();
    private readonly Dictionary<int, DateTime> _lastActivityEmitUtc = new();

    // Called for ALL Photon RaiseEvent messages received by this client
    public void OnEvent(EventData photonEvent)
    {
        try
        {
            ProcessRaceSignals(photonEvent);
            TryDetectMovementActivity(photonEvent);

            if (_silentCodes.Contains(photonEvent.Code))
                return;

            var enableEventsLog = _cfgEnableEventsLog?.Value ?? false;
            var enablePerCodeLogs = _cfgEnablePerCodeLogs?.Value ?? false;

            // Nothing to write? Skip the entire format path to avoid reflection + allocations.
            if (!enableEventsLog && !enablePerCodeLogs)
                return;

            var code = photonEvent.Code;
            var isEvent200 = code == 200;
            var event200DeepDump = _cfgEvent200DeepDump?.Value ?? false;

            string text;
            if (isEvent200 && !event200DeepDump)
            {
                // Cheap one-line summary instead of the full reflection dump. Keeps the
                // event visible in the log without serialising DroneConfiguration/Sprite/etc.
                var paramCount = photonEvent.Parameters?.Count ?? 0;
                text = $"[{DateTime.UtcNow:O}] EVENT Code=200 params={paramCount} (deep-dump disabled)";
            }
            else
            {
                var sb = new StringBuilder(1024);
                sb.AppendLine($"[{DateTime.UtcNow:O}] EVENT Code={code}");

                if (photonEvent.Parameters != null)
                {
                    foreach (var kv in photonEvent.Parameters)
                    {
                        sb.AppendLine($"  Param[{kv.Key}] => {Describe(kv.Value)}");
                    }
                }
                else
                {
                    sb.AppendLine("  (No parameters)");
                }

                text = sb.ToString();
            }

            if (enableEventsLog)
                AppendToFile(text);
            if (enablePerCodeLogs)
                AppendToFile(GetPerCodeFilePath(code), text);
        }
        catch (Exception ex)
        {
            _log.LogWarning($"OnEvent failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void OnPlayerEnteredRoom(Player newPlayer)
    {
        _multiplayerTrackControl?.NotifyActivity(nameof(OnPlayerEnteredRoom));
        _actorToNick[newPlayer.ActorNumber] = newPlayer.NickName;
        if (!string.IsNullOrEmpty(newPlayer.UserId))
            _actorToUserId[newPlayer.ActorNumber] = newPlayer.UserId;
        AppendStateLine($"Player entered room: Actor={newPlayer.ActorNumber} Nick=\"{newPlayer.NickName}\" UserId=\"{newPlayer.UserId}\"");
        AppendRaceEvent("player_entered", new Dictionary<string, object?>
        {
            ["actor"] = newPlayer.ActorNumber,
            ["nick"] = newPlayer.NickName,
            ["user_id"] = string.IsNullOrEmpty(newPlayer.UserId) ? (object?)null : newPlayer.UserId
        });
        _multiplayerTrackControl?.OnPlayerEnteredRoom(newPlayer);
        if (_emitLobbyStatusOnChange?.Value == true) _lobbyStatus?.EmitLobbyStatus();
    }

    public void OnPlayerLeftRoom(Player otherPlayer)
    {
        _multiplayerTrackControl?.NotifyActivity(nameof(OnPlayerLeftRoom));
        _raceParticipants.Remove(otherPlayer.ActorNumber);
        _actorToNick.Remove(otherPlayer.ActorNumber);
        _actorToUserId.Remove(otherPlayer.ActorNumber);
        _lastKnownPosition.Remove(otherPlayer.ActorNumber);
        _lastActivityEmitUtc.Remove(otherPlayer.ActorNumber);
        _actorGmsRun.Remove(otherPlayer.ActorNumber);
        _actorSpawnUtc.Remove(otherPlayer.ActorNumber);
        _actorLastLapUtc.Remove(otherPlayer.ActorNumber);
        AppendStateLine($"Player left room: Actor={otherPlayer.ActorNumber} Nick=\"{otherPlayer.NickName}\"");
        AppendRaceEvent("player_left", new Dictionary<string, object?>
        {
            ["actor"] = otherPlayer.ActorNumber,
            ["nick"] = otherPlayer.NickName
        });
        _multiplayerTrackControl?.OnPlayerLeftRoom(otherPlayer);
        TryEmitRaceEnd();
        if (_emitLobbyStatusOnChange?.Value == true) _lobbyStatus?.EmitLobbyStatus();
    }

    public void OnRoomPropertiesUpdate(PhotonHashtable propertiesThatChanged)
    {
        _multiplayerTrackControl?.NotifyActivity(nameof(OnRoomPropertiesUpdate));
        if (TryGetInt(propertiesThatChanged, "SGSO", out var sharedGameStateOffset) && sharedGameStateOffset == 1)
        {
            if (_raceEndEmitted || _actorLapState.Count > 0)
                StartNewRace("room_sgso_start");
        }

        AppendStateLine(() => $"Room properties updated: {Describe(propertiesThatChanged)}");
        _multiplayerTrackControl?.OnRoomPropertiesUpdate(propertiesThatChanged);
    }

    public void OnPlayerPropertiesUpdate(Player targetPlayer, PhotonHashtable changedProps)
    {
        _multiplayerTrackControl?.NotifyActivity(nameof(OnPlayerPropertiesUpdate));
        _actorToNick[targetPlayer.ActorNumber] = targetPlayer.NickName;
        UpdateRaceStateFromProperties(targetPlayer.ActorNumber, changedProps);
        AppendStateLine(
            () => $"Player properties updated: Actor={targetPlayer.ActorNumber} Nick=\"{targetPlayer.NickName}\" {Describe(changedProps)}");
        _multiplayerTrackControl?.OnPlayerPropertiesUpdate(targetPlayer, changedProps);
    }

    public void OnMasterClientSwitched(Player newMasterClient)
    {
        _multiplayerTrackControl?.NotifyActivity(nameof(OnMasterClientSwitched));
        AppendStateLine($"Master client switched: Actor={newMasterClient.ActorNumber} Nick=\"{newMasterClient.NickName}\"");
        _multiplayerTrackControl?.OnMasterClientSwitched(newMasterClient);
        if (_emitLobbyStatusOnChange?.Value == true) _lobbyStatus?.EmitLobbyStatus();
    }

    // ── IConnectionCallbacks ─────────────────────────────────────────────────

    public void OnConnected()
    {
        _log.LogInfo("[Photon] Connected to Photon server.");
        try { ApplyPhotonNetworkTuning(); } catch { /* best effort */ }
    }

    public void OnConnectedToMaster()
    {
        _log.LogInfo("[Photon] Connected to Master server.");
        try { ApplyPhotonNetworkTuning(); } catch { /* best effort */ }
    }

    public void OnDisconnected(DisconnectCause cause)
    {
        _log.LogWarning($"[Photon] Disconnected from Photon: cause={cause}");
        AppendStateLine($"Photon disconnected: cause={cause}");
        _photonWasDisconnected = true;
        // Tuning may need to be re-applied on the next reconnect if the peer
        // instance changes. Clearing this flag guarantees the next apply logs.
        _photonTuningApplied = false;
        _lobbyStatus?.EmitLobbyStatus();
        _multiplayerTrackControl?.OnPhotonDisconnected(cause);
    }

    public void OnRegionListReceived(RegionHandler regionHandler) { }
    public void OnCustomAuthenticationResponse(Dictionary<string, object> data) { }
    public void OnCustomAuthenticationFailed(string debugMessage)
    {
        _log.LogWarning($"[Photon] Custom auth failed: {debugMessage}");
    }

    private string Describe(object? value)
    {
        return Describe(value, 0);
    }

    private string Describe(object? value, int depth)
    {
        if (value == null) return "<null>";
        if (depth >= MaxDescribeDepth) return $"<{value.GetType().Name} depth-limit>";

        switch (value)
        {
            case byte b:
                return $"byte {b}";
            case short s:
                return $"short {s}";
            case int i:
                return $"int {i}";
            case long l:
                return $"long {l}";
            case float f:
                return $"float {f}";
            case double d:
                return $"double {d}";
            case bool bo:
                return $"bool {bo}";
            case string str:
                return $"string \"{str}\"";
            case byte[] bytes:
                return $"byte[{bytes.Length}] {BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, MaxBytePreview))}{(bytes.Length > MaxBytePreview ? "..." : "")}";
            case PhotonHashtable ht:
                return DescribeHashtable(ht, depth + 1);
            case IDictionary dict:
                return DescribeDictionary(dict, depth + 1);
            case Array arr:
                return DescribeArray(arr, depth + 1);
            case ICollection col when value is not string:
                return DescribeCollection(col, depth + 1);
            default:
                return DescribeObject(value, depth + 1);
        }
    }

    private string DescribeArray(Array arr, int depth)
    {
        var limit = Math.Min(arr.Length, MaxCollectionItems);
        var parts = new List<string>(limit);

        for (var i = 0; i < limit; i++)
        {
            parts.Add(Describe(arr.GetValue(i), depth));
        }

        return $"{arr.GetType().Name}[{arr.Length}] [{string.Join(", ", parts)}]{(arr.Length > limit ? ", ..." : "")}";
    }

    private string DescribeCollection(ICollection col, int depth)
    {
        var parts = new List<string>();
        var i = 0;

        foreach (var item in col)
        {
            i++;
            if (i > MaxCollectionItems) { parts.Add("..."); break; }
            parts.Add(Describe(item, depth));
        }

        return $"{col.GetType().Name} (Count={col.Count}) [{string.Join(", ", parts)}]";
    }

    private string DescribeHashtable(PhotonHashtable ht, int depth)
    {
        var parts = new List<string>();
        var i = 0;

        foreach (DictionaryEntry de in ht)
        {
            i++;
            if (i > MaxCollectionItems) { parts.Add("..."); break; }
            parts.Add($"{Describe(de.Key, depth)}={Describe(de.Value, depth)}");
        }

        return $"Hashtable({ht.Count}) {{{string.Join(", ", parts)}}}";
    }

    private string DescribeDictionary(IDictionary dict, int depth)
    {
        var parts = new List<string>();
        var i = 0;

        foreach (DictionaryEntry de in dict)
        {
            i++;
            if (i > MaxCollectionItems) { parts.Add("..."); break; }
            parts.Add($"{Describe(de.Key, depth)}={Describe(de.Value, depth)}");
        }

        return $"IDictionary({dict.Count}) {{{string.Join(", ", parts)}}}";
    }

    private string DescribeObject(object value, int depth)
    {
        var type = value.GetType();
        var members = new List<string>();

        foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;

            string formatted;
            try
            {
                formatted = Describe(prop.GetValue(value), depth);
            }
            catch (Exception ex)
            {
                formatted = $"<error:{ex.GetType().Name}>";
            }

            members.Add($"{prop.Name}={formatted}");
            if (members.Count >= MaxCollectionItems) break;
        }

        if (members.Count < MaxCollectionItems)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                string formatted;
                try
                {
                    formatted = Describe(field.GetValue(value), depth);
                }
                catch (Exception ex)
                {
                    formatted = $"<error:{ex.GetType().Name}>";
                }

                members.Add($"{field.Name}={formatted}");
                if (members.Count >= MaxCollectionItems) break;
            }
        }

        if (members.Count == 0)
            return $"{type.Name}: {value}";

        return $"{type.Name} {{{string.Join(", ", members)}}}";
    }

    private void AppendToFile(string text)
    {
        _logQueue.TryAdd((_filePath, text));
    }

    private void AppendToFile(string path, string text)
    {
        _logQueue.TryAdd((path, text));
    }

    private string GetPerCodeFilePath(byte eventCode)
    {
        return Path.Combine(_eventCodeDir, $"event-code-{eventCode}.log");
    }

    /// <summary>True when photon-state logging is enabled. Read by the lazy log paths
    /// to avoid building expensive reflection-dump strings that would be discarded.</summary>
    internal bool StateLogEnabled => _cfgEnableStateLog?.Value ?? true;

    private void AppendStateLine(string message)
    {
        if (!StateLogEnabled) return;
        var line = $"[{DateTime.UtcNow:O}] {message}";
        _logQueue.TryAdd((_stateFilePath, line));
    }

    /// <summary>
    /// Lazy state-line writer — the message factory only runs when state logging is on.
    /// Used on high-frequency Photon callbacks where the message embeds a recursive
    /// reflection dump (Describe), which is too costly to build on the main thread per event.
    /// </summary>
    private void AppendStateLine(Func<string> messageFactory)
    {
        if (!StateLogEnabled) return;
        var line = $"[{DateTime.UtcNow:O}] {messageFactory()}";
        _logQueue.TryAdd((_stateFilePath, line));
    }

    private void CleanOldLogs(string pluginDir, string eventCodeDir, int maxAgeDays)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
            foreach (var dir in new[] { pluginDir, eventCodeDir })
            {
                foreach (var file in Directory.GetFiles(dir, "*.log").Concat(Directory.GetFiles(dir, "*.jsonl")))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
            }
        }
        catch { }
    }

    private void ProcessRaceSignals(EventData photonEvent)
    {
        if (!TryExtractLapTime(photonEvent, out var actor, out var guid, out var lapMs))
            return;

        RecordLapTime(actor, guid, lapMs, "event200");
    }

    /// <summary>
    /// Emits a throttled pilot_active event for the given actor.
    /// Returns true if the event was emitted, false if throttled.
    /// </summary>
    private bool TryEmitThrottledActivity(int actor, string source, string? detail = null)
    {
        var now = DateTime.UtcNow;
        if (_lastActivityEmitUtc.TryGetValue(actor, out var lastEmit)
            && (now - lastEmit).TotalSeconds < ActivityThrottleSecs)
            return false;

        _lastActivityEmitUtc[actor] = now;

        var nick = _actorToNick.TryGetValue(actor, out var n) ? n : "Unknown";
        var data = new Dictionary<string, object?>
        {
            ["actor"] = actor,
            ["nick"] = nick,
            ["source"] = source
        };
        if (detail != null)
            data["detail"] = detail;

        AppendRaceEvent("pilot_active", data);
        return true;
    }

    /// <summary>
    /// Detects player movement from Event 201 position telemetry.
    /// Emits a pilot_active event when a player's drone moves ≥ 2 units,
    /// throttled to once per 5 seconds per player.
    /// </summary>
    private void TryDetectMovementActivity(EventData photonEvent)
    {
        if (photonEvent.Code != 201 || photonEvent.Parameters == null)
            return;

        if (!photonEvent.Parameters.TryGetValue(254, out var actorObj) || !TryConvertToInt(actorObj, out var actor))
            return;

        // Extract position from Event 201 customData: Object[][3] -> [2] -> Object[][4] -> [3] -> Single[12]
        if (!photonEvent.Parameters.TryGetValue(245, out var customDataObj) || customDataObj is not object[] outerArray || outerArray.Length < 3)
            return;

        if (outerArray[2] is not object[] innerArray || innerArray.Length < 4)
            return;

        if (innerArray[3] is not float[] floatData || floatData.Length < 7)
            return;

        var posX = floatData[4];
        var posY = floatData[5];
        var posZ = floatData[6];

        if (_lastKnownPosition.TryGetValue(actor, out var lastPos))
        {
            var dx = posX - lastPos[0];
            var dy = posY - lastPos[1];
            var dz = posZ - lastPos[2];
            var distSq = dx * dx + dy * dy + dz * dz;

            if (distSq >= MovementThreshold * MovementThreshold)
            {
                _lastKnownPosition[actor] = new[] { posX, posY, posZ };
                TryEmitThrottledActivity(actor, "movement");
            }
        }
        else
        {
            // First position — store baseline, no activity event yet
            _lastKnownPosition[actor] = new[] { posX, posY, posZ };
        }
    }

    private bool TryExtractLapTime(EventData photonEvent, out int actor, out string guid, out int lapMs)
    {
        actor = -1;
        guid = string.Empty;
        lapMs = 0;

        if (photonEvent.Code != 200 || photonEvent.Parameters == null)
            return false;

        if (!photonEvent.Parameters.TryGetValue(254, out var actorObj) || !TryConvertToInt(actorObj, out actor))
            return false;

        if (!photonEvent.Parameters.TryGetValue(245, out var payloadObj) || payloadObj is not PhotonHashtable payload)
            return false;

        if (!TryGetInt(payload, (byte)5, out var action) || action != 15)
            return false;

        if (!TryGetInt(payload, (byte)0, out var category) || category != 1)
            return false;

        if (!payload.TryGetValue((byte)4, out var dataObj) || dataObj is not object[] data || data.Length < 3)
            return false;

        if (data[1] is not string key || !key.EndsWith(LapTimesSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!TryConvertToInt(data[2], out lapMs))
            return false;

        guid = key.Substring(0, key.Length - LapTimesSuffix.Length);
        return true;
    }

    private PilotLapState GetOrCreatePilotLapState(int actor, string guid)
    {
        if (_actorLapState.TryGetValue(actor, out var existing))
        {
            if (string.IsNullOrEmpty(existing.Guid))
                existing.Guid = guid;
            if (!_guidLapState.ContainsKey(existing.Guid))
                _guidLapState[existing.Guid] = existing;
            existing.Nick = ResolveNick(actor);
            return existing;
        }

        if (!string.IsNullOrEmpty(guid) && _guidLapState.TryGetValue(guid, out var byGuid))
        {
            byGuid.Actor = actor;
            byGuid.Nick = ResolveNick(actor);
            _actorLapState[actor] = byGuid;
            return byGuid;
        }

        var state = new PilotLapState
        {
            Actor = actor,
            Nick = ResolveNick(actor),
            Guid = guid
        };
        _actorLapState[actor] = state;
        if (!string.IsNullOrEmpty(guid))
            _guidLapState[guid] = state;
        return state;
    }

    private void UpdateRaceStateFromProperties(int actor, PhotonHashtable changedProps)
    {
        if (TryGetInt(changedProps, "GS", out var gs))
        {
            if (gs == 2)
                _raceParticipants.Add(actor);
            TryEmitThrottledActivity(actor, "property_change", $"GS={gs}");
        }

        if (_actorToRaceState.TryGetValue(actor, out var previousRs)
            && TryGetInt(changedProps, "RS", out var nextRs)
            && previousRs >= 5 && nextRs <= 3)
        {
            StartNewRace($"actor_{actor}_rs_reset");
        }

        if (changedProps.TryGetValue("GMS", out var gmsObj) && gmsObj != null)
        {
            TryEmitThrottledActivity(actor, "property_change", "GMS");
            var gmsText = Describe(gmsObj);

            if (TryExtractCheckpointFromGmsText(gmsText, out var checkpointId, out var checkpointLap, out var checkpointTimeSec))
            {
                if (!_actorLastCheckpointId.TryGetValue(actor, out var prevCheckpointId) || !string.Equals(prevCheckpointId, checkpointId, StringComparison.Ordinal))
                {
                    _actorLastCheckpointId[actor] = checkpointId;
                    AppendRaceLine(
                        $"CHECKPOINT actor={actor} nick=\"{ResolveNick(actor)}\" checkpointId={checkpointId} lap={checkpointLap} timeSec={checkpointTimeSec:0.000}");
                    AppendRaceEvent("checkpoint", new Dictionary<string, object?>
                    {
                        ["actor"] = actor,
                        ["nick"] = ResolveNick(actor),
                        ["checkpoint_id"] = checkpointId,
                        ["lap_index"] = checkpointLap,
                        ["elapsed_sec"] = Math.Round(checkpointTimeSec, 3)
                    });
                }
            }

            if (TryExtractLapTimesFromGmsText(gmsText, out var lapTimesSec))
            {
                MergeGmsLapSeries(actor, lapTimesSec);
            }
            else if (!gmsText.Contains("Single[]"))
            {
                // No lap list at all. The game republishes a pilot's GMS, empty, the moment it
                // respawns their drone — about 0.6s before the drone reappears at the start.
                OnGmsRespawn(actor);
            }
        }

        if (TryGetInt(changedProps, "RS", out var rs))
        {
            // Only mark a pilot complete from RS if we observed them at RS<5 first in this
            // race. Without this guard, a StartNewRace() that clears _actorToRaceState will
            // cause the next property update to re-lock pilots whose RS is still >=5 from
            // the previous race, silently dropping all their laps on the new track.
            var hadLowerRs = _actorToRaceState.TryGetValue(actor, out var prevRs) && prevRs < 5;
            _actorToRaceState[actor] = rs;
            TryEmitThrottledActivity(actor, "property_change", $"RS={rs}");
            if (rs >= 5 && hadLowerRs)
            {
                var state = GetOrCreatePilotLapState(actor, string.Empty);
                if (!state.IsComplete)
                {
                    state.IsComplete = true;
                    state.CompletionReason = "race_state_finished";
                    EmitPilotComplete(state);
                }
                TryEmitRaceEnd();
            }
        }
    }

    // A respawn republishes GMS twice, back to back, when a pilot joins or the track changes,
    // and a pilot can press reset twice inside a second. Neither is an attempt worth reporting.
    private static readonly TimeSpan RespawnDebounce = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A pilot's drone has just respawned. Unless this is them arriving — the first spawn a
    /// race sees for them — they reset, and the attempt they abandoned is reported now rather
    /// than when their next lap happens to arrive, which for a pilot who resets and leaves is never.
    /// </summary>
    private void OnGmsRespawn(int actor)
    {
        var now = DateTime.UtcNow;
        var hadSpawn = _actorSpawnUtc.TryGetValue(actor, out var lastSpawn);
        var hadLap = _actorLastLapUtc.TryGetValue(actor, out var lastLap);
        _actorSpawnUtc[actor] = now;

        if (hadSpawn && now - lastSpawn < RespawnDebounce)
            return;
        if (!hadSpawn && !hadLap)
            return; // arriving: the track change, joining, or this plugin starting
        if (_actorLapState.TryGetValue(actor, out var state) && state.IsComplete)
            return;

        var lapsInRun = _actorGmsRun.TryGetValue(actor, out var run) ? run.Count : 0;
        // Flying on from a lap, the abandoned attempt began as that lap ended. Otherwise it
        // began at the last respawn, so it includes the wait there and the run-up to the line.
        var fromLap = hadLap && (!hadSpawn || lastLap > lastSpawn);
        var attemptMs = (int)(now - (fromLap ? lastLap : lastSpawn)).TotalMilliseconds;

        ResetPilotState(actor, "respawn", new Dictionary<string, object?>
        {
            ["attempt_ms"] = attemptMs,
            ["attempt_from"] = fromLap ? "lap" : "respawn",
            ["laps_in_run"] = lapsInRun,
        });
    }

    private void MergeGmsLapSeries(int actor, List<float> lapTimesSec)
    {
        var incoming = lapTimesSec.Select(v => (int)Math.Round(v * 1000d)).ToList();
        if (incoming.Count == 0)
            return;

        var state = GetOrCreatePilotLapState(actor, string.Empty);
        if (state.IsComplete)
            return;

        // GMS carries the laps of the pilot's current run and grows by one as each lap ends;
        // a respawn empties it (OnGmsRespawn). So the list is compared with the run last seen,
        // not with every lap the pilot has flown — event200 laps included — this race.
        if (!_actorGmsRun.TryGetValue(actor, out var run))
        {
            if (_needGmsBaseline && state.LapTimesMs.Count == 0 && !_actorSpawnUtc.ContainsKey(actor))
            {
                // First sight of this pilot since the plugin started, part way through a run
                // that may hold laps flown before it. Remember them, so only laps added from
                // here on are recorded. A pilot seen respawning since is on a run that began
                // after we did, and after a race reset the game starts every run afresh.
                _actorGmsRun[actor] = incoming;
                _log.LogInfo($"[Recording] GMS baseline set for actor {actor}: {incoming.Count} pre-session lap(s) skipped");
                return;
            }
            run = new List<int>();
        }

        if (IsPrefix(run, incoming))
            return; // nothing new

        if (!IsPrefix(incoming, run))
        {
            // The run started again without the respawn that announces it reaching us.
            // Report the reset late rather than not at all.
            ResetPilotState(actor, "gms_series_mismatch");
            run = new List<int>();
        }

        for (var i = run.Count; i < incoming.Count; i++)
            RecordLapTime(actor, string.Empty, incoming[i], "gms");
        _actorGmsRun[actor] = incoming;
        _actorLastLapUtc[actor] = DateTime.UtcNow;
    }

    private void RecordLapTime(int actor, string guid, int lapMs, string source)
    {
        if (DateTime.UtcNow < _suppressEvent200Until)
        {
            _log.LogWarning($"[Recording] Lap suppressed (track change grace): actor={actor} lapMs={lapMs} source={source}");
            return;
        }

        var minMs = _minLapMs.Value;
        if (minMs > 0 && lapMs < minMs)
        {
            _log.LogWarning($"[Recording] Lap ignored (too short): actor={actor} lapMs={lapMs} minLapMs={minMs}");
            return;
        }

        var state = GetOrCreatePilotLapState(actor, guid);
        if (state.IsComplete)
            return;

        var nextLapIndex = state.LapTimesMs.Count;
        if (nextLapIndex > 0 && state.LapTimesMs[nextLapIndex - 1] == lapMs)
            return;

        state.LapTimesMs.Add(lapMs);
        _raceParticipants.Add(actor);

        var lapNumber = state.LapTimesMs.Count;
        var deltaPrev = lapNumber > 1 ? lapMs - state.LapTimesMs[lapNumber - 2] : (int?)null;
        var bestBefore = lapNumber > 1 ? state.LapTimesMs.Take(lapNumber - 1).Min() : lapMs;
        var deltaBest = lapNumber > 1 ? lapMs - bestBefore : (int?)null;

        AppendRaceLine(
            $"LAP actor={actor} nick=\"{state.Nick}\" guid={state.Guid} source={source} lap={lapNumber} ms={lapMs} sec={ToSeconds(lapMs)} deltaPrevMs={FormatNullable(deltaPrev)} deltaBestMs={FormatNullable(deltaBest)}");
        _actorToUserId.TryGetValue(actor, out var steamId);
        AppendRaceEvent("lap_recorded", new Dictionary<string, object?>
        {
            ["actor"] = actor,
            ["nick"] = state.Nick,
            ["pilot_guid"] = state.Guid,
            ["steam_id"] = string.IsNullOrEmpty(steamId) ? (object?)null : steamId,
            ["source"] = source,
            ["lap_number"] = lapNumber,
            ["lap_ms"] = lapMs,
            ["lap_sec"] = Math.Round(lapMs / 1000d, 3),
            ["delta_prev_ms"] = deltaPrev,
            ["delta_best_ms"] = deltaBest
        });

        var maxLaps = _maxLapsPerRace.Value;
        if (maxLaps > 0 && !state.IsComplete && lapNumber >= maxLaps)
        {
            state.IsComplete = true;
            state.CompletionReason = "lap_count_reached";
            EmitPilotComplete(state);
        }

        TryEmitRaceEnd();
    }

    private static bool IsPrefix(IReadOnlyList<int> source, IReadOnlyList<int> candidatePrefix)
    {
        if (candidatePrefix.Count > source.Count)
            return false;

        for (var i = 0; i < candidatePrefix.Count; i++)
        {
            if (source[i] != candidatePrefix[i])
                return false;
        }

        return true;
    }

    private void ResetPilotState(int actor, string reason, Dictionary<string, object?>? detail = null)
    {
        if (_actorLapState.TryGetValue(actor, out var state))
        {
            if (!string.IsNullOrEmpty(state.Guid))
                _guidLapState.Remove(state.Guid);
        }

        _actorLapState.Remove(actor);
        _actorLastCheckpointId.Remove(actor);
        _actorGmsRun.Remove(actor);

        var payload = new Dictionary<string, object?>
        {
            ["actor"] = actor,
            ["nick"] = ResolveNick(actor),
            ["reason"] = reason
        };
        var extra = string.Empty;
        if (detail != null)
        {
            foreach (var kv in detail)
                payload[kv.Key] = kv.Value;
            extra = " " + string.Join(" ", detail.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        AppendRaceLine($"PILOT_RESET actor={actor} nick=\"{ResolveNick(actor)}\" reason={reason}{extra}");
        AppendRaceEvent("pilot_reset", payload);
    }

    /// <summary>
    /// The session_started payload. Sent on every (re)connect and once at startup; the server
    /// keys the session row off it, so both sites must agree.
    /// </summary>
    private Dictionary<string, object?> SessionStartedPayload() => new()
    {
        ["event_type"]    = "session_started",
        ["timestamp_utc"] = DateTime.UtcNow.ToString("O"),
        ["session_id"]    = _sessionId,
        ["race_id"]       = _raceId,
        ["race_ordinal"]  = _raceOrdinal,
        ["event_ordinal"] = _raceEventOrdinal,
        ["plugin"]        = PluginName,
        ["version"]       = PluginVersion,
        ["buildMarker"]   = BuildMarker,
        // Tells the server whether chat backlog is suppressed at source ("Receive"/
        // "SuppressHistory") or whether it must dedupe on (session_id, chat_id) itself
        // ("Legacy"). See Features/Chat/ChatCaptureService.cs.
        ["chat_capture_mode"] = ChatCaptureService.Mode.ToString(),
    };

    private void StartNewRace(string reason)
    {
        var previousRaceId = _raceId;
        _raceId = CreateRaceId();
        _raceOrdinal++;
        _raceEventOrdinal = 0;
        _raceEndEmitted = false;
        _actorLapState.Clear();
        _guidLapState.Clear();
        _actorToRaceState.Clear();
        _raceParticipants.Clear();
        _actorLastCheckpointId.Clear();
        _actorGmsRun.Clear();
        _actorSpawnUtc.Clear();
        _actorLastLapUtc.Clear();
        _needGmsBaseline = false;
        CheckpointHookService.ResetGateTracking();
        _competitionClient?.SetRaceContext(_raceId, _raceOrdinal);
        AppendRaceLine($"RACE_RESET reason={reason}");
        AppendRaceEvent("race_reset", new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["previous_race_id"] = previousRaceId,
            ["race_ordinal"] = _raceOrdinal
        });
    }

    private bool TryExtractLapTimesFromGmsText(string gmsText, out List<float> lapTimesSec)
    {
        lapTimesSec = new List<float>();

        var matches = GmsLapArrayRegex.Matches(gmsText);
        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups["count"].Value, out var expectedCount))
                continue;

            var values = new List<float>();
            var valueMatches = GmsLapValueRegex.Matches(match.Groups["vals"].Value);
            foreach (Match valueMatch in valueMatches)
            {
                if (float.TryParse(valueMatch.Groups["v"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
                    values.Add(value);
            }

            if (values.Count != expectedCount)
                continue;
            var gmsLapCap = _maxLapsPerRace.Value > 0 ? _maxLapsPerRace.Value : 100;
            if (values.Count == 0 || values.Count > gmsLapCap)
                continue;
            if (values.Any(v => v <= 1f || v > 600f))
                continue;

            if (values.Count > lapTimesSec.Count)
                lapTimesSec = values;
        }

        return lapTimesSec.Count > 0;
    }

    private bool TryExtractCheckpointFromGmsText(string gmsText, out string checkpointId, out int lap, out float timeSec)
    {
        checkpointId = string.Empty;
        lap = 0;
        timeSec = 0f;

        var match = GmsCheckpointRegex.Match(gmsText);
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups["lap"].Value, out lap))
            return false;
        if (!float.TryParse(match.Groups["time"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out timeSec))
            return false;

        checkpointId = match.Groups["id"].Value;
        return checkpointId.Length > 0;
    }

    private void EmitPilotComplete(PilotLapState state)
    {
        var laps = string.Join(",", state.LapTimesMs);
        var totalMs = state.LapTimesMs.Take(ClassicRaceLapCount).Sum();
        AppendRaceLine(
            $"PILOT_COMPLETE actor={state.Actor} nick=\"{state.Nick}\" guid={state.Guid} reason={state.CompletionReason} lapsLogged={state.LapTimesMs.Count} lapTimesMs=[{laps}] totalMs={totalMs} totalSec={ToSeconds(totalMs)}");
        AppendRaceEvent("pilot_complete", new Dictionary<string, object?>
        {
            ["actor"] = state.Actor,
            ["nick"] = state.Nick,
            ["pilot_guid"] = state.Guid,
            ["reason"] = state.CompletionReason,
            ["laps_logged"] = state.LapTimesMs.Count,
            ["lap_times_ms"] = state.LapTimesMs.ToArray(),
            ["total_ms"] = totalMs,
            ["total_sec"] = Math.Round(totalMs / 1000d, 3)
        });
    }

    private void TryEmitRaceEnd()
    {
        if (_raceEndEmitted || _raceParticipants.Count == 0)
            return;

        foreach (var actor in _raceParticipants)
        {
            var pilotDone = _actorLapState.TryGetValue(actor, out var pilot) && pilot.IsComplete;
            var rsDone = _actorToRaceState.TryGetValue(actor, out var rs) && rs >= 5;
            if (!pilotDone && !rsDone)
                return;
        }

        _raceEndEmitted = true;

        var ranked = _actorLapState.Values
            .Where(p => p.LapTimesMs.Count >= ClassicRaceLapCount)
            .Select(p => new
            {
                p.Actor,
                p.Nick,
                TotalMs = p.LapTimesMs.Take(ClassicRaceLapCount).Sum()
            })
            .OrderBy(p => p.TotalMs)
            .ToList();

        if (ranked.Count > 0)
        {
            var winner = ranked[0];
            AppendRaceLine(
                $"RACE_END participants={_raceParticipants.Count} completed={_raceParticipants.Count} winnerActor={winner.Actor} winnerNick=\"{winner.Nick}\" winnerTotalMs={winner.TotalMs} winnerTotalSec={ToSeconds(winner.TotalMs)}");
            AppendRaceEvent("race_end", new Dictionary<string, object?>
            {
                ["participants"] = _raceParticipants.Count,
                ["completed"] = _raceParticipants.Count,
                ["winner_actor"] = winner.Actor,
                ["winner_nick"] = winner.Nick,
                ["winner_total_ms"] = winner.TotalMs,
                ["winner_total_sec"] = Math.Round(winner.TotalMs / 1000d, 3)
            });
            return;
        }

        AppendRaceLine($"RACE_END participants={_raceParticipants.Count} completed={_raceParticipants.Count}");
        AppendRaceEvent("race_end", new Dictionary<string, object?>
        {
            ["participants"] = _raceParticipants.Count,
            ["completed"] = _raceParticipants.Count
        });
    }

    private void AppendRaceLine(string message)
    {
        var line = $"[{DateTime.UtcNow:O}] {message}";
        _logQueue.TryAdd((_raceFilePath, line));
        _log.LogInfo(line);
    }

    private void AppendRaceEvent(string eventType, Dictionary<string, object?> payload)
    {
        payload["event_type"] = eventType;
        payload["timestamp_utc"] = DateTime.UtcNow.ToString("O");
        payload["session_id"] = _sessionId;
        payload["race_id"] = _raceId;
        payload["race_ordinal"] = _raceOrdinal;
        payload["event_ordinal"] = ++_raceEventOrdinal;

        var json = SerializeJsonObject(payload);
        if (_cfgEnableRaceJsonl?.Value ?? true)
            _logQueue.TryAdd((_raceJsonFilePath, json));
        _competitionClient?.EnqueueEvent(json);
    }

    private string CreateRaceId()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static string SerializeJsonObject(IDictionary<string, object?> map)
    {
        var sb = new StringBuilder(256);
        sb.Append('{');
        var first = true;
        foreach (var kv in map)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(JsonEscape(kv.Key)).Append('"').Append(':').Append(SerializeJsonValue(kv.Value));
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string SerializeJsonValue(object? value)
    {
        if (value == null) return "null";

        switch (value)
        {
            case string str:
                return "\"" + JsonEscape(str) + "\"";
            case bool bo:
                return bo ? "true" : "false";
            case byte b:
                return b.ToString(CultureInfo.InvariantCulture);
            case short s:
                return s.ToString(CultureInfo.InvariantCulture);
            case int i:
                return i.ToString(CultureInfo.InvariantCulture);
            case long l:
                return l.ToString(CultureInfo.InvariantCulture);
            case float f:
                return f.ToString("0.###", CultureInfo.InvariantCulture);
            case double d:
                return d.ToString("0.###", CultureInfo.InvariantCulture);
            case decimal m:
                return m.ToString(CultureInfo.InvariantCulture);
            case IEnumerable<int> ints:
                return "[" + string.Join(",", ints.Select(x => x.ToString(CultureInfo.InvariantCulture))) + "]";
            case IEnumerable<string> strings:
                return "[" + string.Join(",", strings.Select(x => "\"" + JsonEscape(x) + "\"")) + "]";
            case IDictionary<string, object?> dict:
                return SerializeJsonObject(dict);
            case System.Collections.IEnumerable enumerable:
            {
                var parts = new List<string>();
                foreach (var item in enumerable)
                    parts.Add(SerializeJsonValue(item));
                return "[" + string.Join(",", parts) + "]";
            }
            default:
                return "\"" + JsonEscape(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty) + "\"";
        }
    }

    private static string JsonEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

    private string ResolveNick(int actor)
    {
        return _actorToNick.TryGetValue(actor, out var nick) ? nick : $"Actor{actor}";
    }

    private static bool TryGetInt(PhotonHashtable map, object key, out int value)
    {
        value = 0;
        return map.TryGetValue(key, out var raw) && TryConvertToInt(raw, out value);
    }

    private static bool TryConvertToInt(object? raw, out int value)
    {
        switch (raw)
        {
            case byte b:
                value = b;
                return true;
            case short s:
                value = s;
                return true;
            case int i:
                value = i;
                return true;
            case long l when l is <= int.MaxValue and >= int.MinValue:
                value = (int)l;
                return true;
            case string str when int.TryParse(str, out var parsed):
                value = parsed;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static string ToSeconds(int ms)
    {
        return (ms / 1000d).ToString("0.000");
    }

    private static string FormatNullable(int? value)
    {
        return value.HasValue ? value.Value.ToString() : "NA";
    }
}
