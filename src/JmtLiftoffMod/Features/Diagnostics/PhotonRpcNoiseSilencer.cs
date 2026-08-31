using System;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace JmtLiftoffMod.Features.Diagnostics;

/// <summary>
/// Silences high-frequency PUN warnings that were starving the Unity main thread and
/// eventually triggering Photon ServerTimeout disconnects.
///
/// Observed symptom (see Logs/JMT-MEDIUM/Player-bot1.log):
///   - Remote pilots constantly emit RPCPlayerReset for a viewID that the local client
///     has already destroyed, so PUN's ExecuteRpc logs the "Received RPC ... but this
///     PhotonView does not exist!" warning — hundreds per second in a full lobby.
///   - Every Debug.LogWarning captures a managed stack trace synchronously on the main
///     thread. With enough volume this stretches individual Update() ticks past Photon's
///     30s keepalive window, and the server cuts the session (cause=ServerTimeout).
///
/// Mitigation (both layered, independent):
///   1. Disable stack-trace capture for LogType.Warning (and LogType.Error) — this is
///      by far the dominant cost per log call. Exceptions keep stack traces.
///   2. Harmony-prefix Debug.LogWarning(object [, Object]) and drop messages whose text
///      matches the known PUN-orphan-view pattern. A counter is summarised periodically
///      so we still know the spam is happening.
/// </summary>
internal sealed class PhotonRpcNoiseSilencer : IDisposable
{
    // Substring used to identify the target PUN warning. Matches both the "Was remote PV"
    // and "View was/is ours" branches of PhotonNetwork.ExecuteRpc.
    private const string OrphanViewMarker = "but this PhotonView does not exist";

    private static ManualLogSource? s_log;
    private static long s_suppressedCount;
    private static long s_lastSummaryUnixMs;
    private const long SummaryIntervalMs = 30_000;

    private readonly Harmony _harmony;
    private readonly ManualLogSource _logSource;
    private bool _installed;

    public PhotonRpcNoiseSilencer(ManualLogSource log, string harmonyId)
    {
        _logSource = log;
        _harmony = new Harmony(harmonyId + ".rpcnoise");
        s_log = log;
    }

    public bool Install()
    {
        if (_installed)
            return true;

        try
        {
            // 1. Drop stack-trace capture for warnings and errors. Huge per-call saving —
            //    stack capture is the dominant cost of Debug.LogWarning. Exceptions keep
            //    their stack trace so genuine crashes are still diagnosable.
            Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Assert, StackTraceLogType.None);
            // LogType.Exception deliberately left at default — we still want stacks when
            // something actually throws.
            _logSource.LogInfo("[RpcNoise] Stack traces disabled for Log/Warning/Error/Assert.");

            // 2. Prefix-patch Debug.LogWarning(object) and Debug.LogWarning(object, Object)
            //    so the known orphan-view spam never reaches Unity's log pipeline.
            var target1 = typeof(Debug).GetMethod(
                nameof(Debug.LogWarning),
                BindingFlags.Static | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(object) },
                modifiers: null);
            var target2 = typeof(Debug).GetMethod(
                nameof(Debug.LogWarning),
                BindingFlags.Static | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(object), typeof(UnityEngine.Object) },
                modifiers: null);

            var prefix = new HarmonyMethod(
                typeof(PhotonRpcNoiseSilencer).GetMethod(
                    nameof(LogWarningPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic));

            if (target1 != null)
                _harmony.Patch(target1, prefix: prefix);
            else
                _logSource.LogWarning("[RpcNoise] Debug.LogWarning(object) not found — skip.");

            if (target2 != null)
                _harmony.Patch(target2, prefix: prefix);
            else
                _logSource.LogWarning("[RpcNoise] Debug.LogWarning(object, Object) not found — skip.");

            _installed = true;
            _logSource.LogInfo("[RpcNoise] Orphan-PhotonView warning suppressor installed.");
            return true;
        }
        catch (Exception ex)
        {
            _logSource.LogWarning($"[RpcNoise] Install failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // Prefix for UnityEngine.Debug.LogWarning. Return false to skip the original call.
    //
    // We must be extremely cheap here — this runs on every single Debug.LogWarning in the
    // process. Allocations, reflection, and regex are off the table.
    private static bool LogWarningPrefix(object message)
    {
        if (message == null)
            return true;

        // Fast path: only examine strings. Other object payloads (rare) pass through.
        if (message is string s)
        {
            if (s.IndexOf(OrphanViewMarker, StringComparison.Ordinal) >= 0)
            {
                Interlocked.Increment(ref s_suppressedCount);
                MaybeEmitSummary();
                return false; // suppress
            }
        }

        return true;
    }

    private static void MaybeEmitSummary()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var last = Interlocked.Read(ref s_lastSummaryUnixMs);
        if (now - last < SummaryIntervalMs)
            return;

        // Only one thread wins the swap — others skip this round.
        if (Interlocked.CompareExchange(ref s_lastSummaryUnixMs, now, last) != last)
            return;

        var count = Interlocked.Exchange(ref s_suppressedCount, 0);
        if (count > 0)
            s_log?.LogInfo($"[RpcNoise] Suppressed {count} orphan-PhotonView warnings in the last {SummaryIntervalMs / 1000}s.");
    }

    public void Dispose()
    {
        if (!_installed)
            return;

        try
        {
            _harmony.UnpatchSelf();
        }
        catch { /* best effort */ }

        // Restore default Unity behaviour so host app isn't surprised by our global change
        // if the plugin is unloaded.
        try
        {
            Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.ScriptOnly);
            Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.ScriptOnly);
            Application.SetStackTraceLogType(LogType.Assert, StackTraceLogType.ScriptOnly);
        }
        catch { /* best effort */ }

        _installed = false;
        s_log = null;
    }
}
