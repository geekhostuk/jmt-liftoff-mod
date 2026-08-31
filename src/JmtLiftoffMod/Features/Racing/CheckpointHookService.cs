using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace JmtLiftoffMod.Features.Racing;

/// <summary>
/// Installs a Harmony postfix on RaceCheckpoint.Trigger() to capture gate-by-gate timing.
/// Emits gate_passed and sector_split data via a callback to the Plugin.
///
/// The drone parameter type is obfuscated, so we resolve it at runtime by finding the
/// Trigger method on RaceCheckpoint (which is not obfuscated) and patching it dynamically.
/// </summary>
internal sealed class CheckpointHookService : IDisposable
{
    private readonly ManualLogSource _log;
    private readonly Harmony _harmony;
    private bool _installed;

    // Static callback — set by the owning instance so the static Harmony postfix can invoke it.
    // Only one instance of this service should exist at a time.
    private static Action<GatePassedData>? s_onGatePassed;

    // Per-pilot gate tracking for sector split computation (actor -> ordered list of gate times)
    private static readonly Dictionary<int, List<GateRecord>> s_pilotGates = new();

    public CheckpointHookService(ManualLogSource log, string harmonyId)
    {
        _log = log;
        _harmony = new Harmony(harmonyId + ".checkpointhook");
    }

    /// <summary>Data emitted when a drone passes through a checkpoint gate.</summary>
    public sealed class GatePassedData
    {
        public int Actor;
        public string Nick = "";
        public int CheckpointId;
        public string TriggerId = "";
        public float GameTimeSec;
        public DateTime TimestampUtc;
    }

    /// <summary>Data emitted when a sector split can be computed between two consecutive gates.</summary>
    public sealed class SectorSplitData
    {
        public int Actor;
        public string Nick = "";
        public int SectorIndex;
        public int FromGate;
        public int ToGate;
        public int SectorMs;
        public float GameTimeSec;
    }

    private sealed class GateRecord
    {
        public int CheckpointId;
        public float GameTimeSec;
    }

    /// <summary>
    /// Set the callback that will be invoked (on the Unity main thread) when a gate is passed.
    /// </summary>
    public void SetCallback(Action<GatePassedData> onGatePassed)
    {
        s_onGatePassed = onGatePassed;
    }

    /// <summary>Clears per-pilot gate tracking (call on race reset).</summary>
    public static void ResetGateTracking()
    {
        s_pilotGates.Clear();
    }

    /// <summary>
    /// Attempts to find RaceCheckpoint.Trigger() at runtime and install a Harmony postfix.
    /// Returns true if the patch was installed successfully.
    /// </summary>
    public bool TryInstall()
    {
        try
        {
            // Find RaceCheckpoint type — it's not obfuscated
            var raceCheckpointType = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.Name == "RaceCheckpoint");

            if (raceCheckpointType == null)
            {
                _log.LogWarning("[CheckpointHook] RaceCheckpoint type not found — gate timing disabled.");
                return false;
            }

            // Find the Trigger method (single parameter of the obfuscated drone type)
            var triggerMethod = raceCheckpointType.GetMethod("Trigger",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (triggerMethod == null)
            {
                _log.LogWarning("[CheckpointHook] RaceCheckpoint.Trigger() method not found — gate timing disabled.");
                return false;
            }

            var droneParamType = triggerMethod.GetParameters().FirstOrDefault()?.ParameterType;
            _log.LogInfo($"[CheckpointHook] Found RaceCheckpoint.Trigger({droneParamType?.Name ?? "?"}) — installing postfix.");

            // Apply Harmony postfix
            var postfix = new HarmonyMethod(typeof(CheckpointHookService).GetMethod(nameof(TriggerPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            _harmony.Patch(triggerMethod, postfix: postfix);

            _installed = true;
            _log.LogInfo("[CheckpointHook] Harmony patch installed successfully.");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[CheckpointHook] Failed to install: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Harmony postfix for RaceCheckpoint.Trigger().
    /// __instance is the RaceCheckpoint, __0 is the drone parameter (obfuscated type).
    /// </summary>
    private static void TriggerPostfix(object __instance, object __0)
    {
        try
        {
            if (s_onGatePassed == null) return;

            // One-time debug dump of drone type structure to help resolve actor mapping
            if (!s_droneTypeDumped)
            {
                s_droneTypeDumped = true;
                DumpDroneTypeInfo(__0);
            }

            // Extract checkpoint info from __instance (RaceCheckpoint)
            var checkpointIdProp = __instance.GetType().GetProperty("CheckpointID");
            var triggerIdProp = __instance.GetType().GetProperty("TriggerId");

            var checkpointId = checkpointIdProp != null ? (int)checkpointIdProp.GetValue(__instance) : -1;
            var triggerId = triggerIdProp != null ? triggerIdProp.GetValue(__instance) as string ?? "" : "";

            // Resolve drone → actor number
            var actor = ResolveDroneActor(__0);
            if (!actor.HasValue) return; // Can't identify who passed the gate

            var gameTime = Time.time;
            var data = new GatePassedData
            {
                Actor = actor.Value,
                CheckpointId = checkpointId,
                TriggerId = triggerId,
                GameTimeSec = gameTime,
                TimestampUtc = DateTime.UtcNow,
            };

            s_onGatePassed.Invoke(data);

            // Track for sector split computation
            if (!s_pilotGates.TryGetValue(actor.Value, out var gates))
            {
                gates = new List<GateRecord>();
                s_pilotGates[actor.Value] = gates;
            }
            gates.Add(new GateRecord { CheckpointId = checkpointId, GameTimeSec = gameTime });
        }
        catch
        {
            // Postfix must never throw — would crash the game
        }
    }

    private static bool s_droneTypeDumped;

    /// <summary>
    /// Dumps the drone object's type info to BepInEx log so we can figure out how to resolve the actor.
    /// Only runs once.
    /// </summary>
    private static void DumpDroneTypeInfo(object drone)
    {
        try
        {
            var type = drone.GetType();
            var lines = new List<string>
            {
                $"[CheckpointHook] DRONE TYPE DUMP: {type.FullName}",
                $"[CheckpointHook]   IsMonoBehaviour: {drone is MonoBehaviour}",
                $"[CheckpointHook]   IsComponent: {drone is Component}",
            };

            // Check if it's a Component and try to find PhotonView
            if (drone is Component comp)
            {
                var pv = comp.GetComponentInParent<PhotonView>();
                lines.Add($"[CheckpointHook]   PhotonView (parent): {(pv != null ? $"viewId={pv.ViewID} owner={pv.Owner?.ActorNumber}" : "null")}");
                pv = comp.GetComponentInChildren<PhotonView>();
                lines.Add($"[CheckpointHook]   PhotonView (children): {(pv != null ? $"viewId={pv.ViewID} owner={pv.Owner?.ActorNumber}" : "null")}");

                // Check root object
                var root = comp.transform.root;
                pv = root.GetComponentInChildren<PhotonView>();
                lines.Add($"[CheckpointHook]   PhotonView (root): {(pv != null ? $"viewId={pv.ViewID} owner={pv.Owner?.ActorNumber}" : "null")}");
            }

            // Dump fields that might contain player/actor info
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var fieldType = field.FieldType;
                if (fieldType == typeof(PhotonView) ||
                    fieldType == typeof(Photon.Realtime.Player) ||
                    fieldType == typeof(int) ||
                    fieldType.Name.Contains("Player") ||
                    fieldType.Name.Contains("Photon") ||
                    fieldType.Name.Contains("Actor") ||
                    fieldType.Name.Contains("owner") ||
                    fieldType.Name.Contains("View"))
                {
                    object? val = null;
                    try { val = field.GetValue(drone); } catch { val = "<error>"; }
                    lines.Add($"[CheckpointHook]   Field: {field.Name} ({fieldType.Name}) = {val}");
                }
            }

            // Also dump properties
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var propType = prop.PropertyType;
                if (propType == typeof(PhotonView) ||
                    propType == typeof(Photon.Realtime.Player) ||
                    propType.Name.Contains("Player") ||
                    propType.Name.Contains("Photon") ||
                    propType.Name.Contains("View"))
                {
                    object? val = null;
                    try { if (prop.CanRead) val = prop.GetValue(drone); } catch { val = "<error>"; }
                    lines.Add($"[CheckpointHook]   Property: {prop.Name} ({propType.Name}) = {val}");
                }
            }

            foreach (var line in lines)
                BepInEx.Logging.Logger.CreateLogSource("CheckpointHook").LogInfo(line);
        }
        catch (Exception ex)
        {
            BepInEx.Logging.Logger.CreateLogSource("CheckpointHook").LogWarning($"[CheckpointHook] Drone dump failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Attempts to resolve a drone object to a Photon actor number.
    /// Strategy: find PhotonView on the drone's GameObject.
    /// </summary>
    private static int? ResolveDroneActor(object drone)
    {
        try
        {
            // Strategy 1: drone is a MonoBehaviour → get gameObject → find PhotonView
            if (drone is MonoBehaviour mb)
            {
                var pv = mb.GetComponentInParent<PhotonView>();
                if (pv?.Owner != null)
                    return pv.Owner.ActorNumber;

                // Also check children
                pv = mb.GetComponentInChildren<PhotonView>();
                if (pv?.Owner != null)
                    return pv.Owner.ActorNumber;
            }

            // Strategy 2: look for PhotonView field via reflection
            var droneType = drone.GetType();
            foreach (var field in droneType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType == typeof(PhotonView))
                {
                    var pv = field.GetValue(drone) as PhotonView;
                    if (pv?.Owner != null)
                        return pv.Owner.ActorNumber;
                }
            }

            // Strategy 3: look for properties that return PhotonView
            foreach (var prop in droneType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (prop.PropertyType == typeof(PhotonView) && prop.CanRead)
                {
                    try
                    {
                        var pv = prop.GetValue(drone) as PhotonView;
                        if (pv?.Owner != null)
                            return pv.Owner.ActorNumber;
                    }
                    catch { /* skip inaccessible properties */ }
                }
            }
        }
        catch { /* best effort */ }

        return null;
    }

    /// <summary>
    /// Computes sector split data if two consecutive gates have been recorded for a pilot.
    /// Call after emitting gate_passed. Returns null if no split can be computed.
    /// </summary>
    public SectorSplitData? TryComputeSectorSplit(int actor, string nick)
    {
        if (!s_pilotGates.TryGetValue(actor, out var gates) || gates.Count < 2)
            return null;

        var prev = gates[gates.Count - 2];
        var curr = gates[gates.Count - 1];
        var sectorMs = (int)((curr.GameTimeSec - prev.GameTimeSec) * 1000f);

        if (sectorMs <= 0) return null;

        return new SectorSplitData
        {
            Actor = actor,
            Nick = nick,
            SectorIndex = gates.Count - 1,
            FromGate = prev.CheckpointId,
            ToGate = curr.CheckpointId,
            SectorMs = sectorMs,
            GameTimeSec = curr.GameTimeSec,
        };
    }

    public void Dispose()
    {
        if (_installed)
        {
            _harmony.UnpatchSelf();
            _installed = false;
        }
        s_onGatePassed = null;
        s_pilotGates.Clear();
    }
}
