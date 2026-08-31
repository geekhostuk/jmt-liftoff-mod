using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace JmtLiftoffMod.Features.Chat;

/// <summary>
/// Captures incoming multiplayer chat messages.
///
/// The message text is read from
///   ChatWindowPanel.GenerateUserMessage(string userId, string userName, string message, Color ledColor)
/// because that is the only place the three strings appear together already unpacked.
///
/// That method is a *rendering* call, not a receive call, and it is reached two ways:
///
///   OnChatMessageReceived(msg)   -> GenerateUserMessage(player, msg) -> GenerateUserMessage(4-arg)
///   OnEnable()                   -> GenerateChatFromHistory()        -> GenerateUserMessage(4-arg)  [per retained message]
///
/// The second path is a redraw of the retained backlog. The chat panel is disabled and
/// re-enabled when the race scene reloads, so every past message was being re-emitted as a
/// live <c>chat_message</c> event once per race — votes and commands typed once were acted
/// on again and again by the server. See docs/chat-capture.md.
///
/// The fix is to gate emission on being inside the receive call, so a redraw emits nothing.
/// Both gate methods are optional: if Liftoff renames them, capture degrades to a weaker
/// mode rather than going silent. The active mode is logged at startup and reported to
/// the server in <c>session_started</c> as <c>chat_capture_mode</c>, so a server can tell
/// whether it is getting the de-duplicated guarantee or has to dedupe on
/// <c>(session_id, chat_id)</c> itself.
/// </summary>
internal sealed class ChatCaptureService : IDisposable
{
    /// <summary>How emission is gated, best first. Reported in the startup log.</summary>
    internal enum CaptureMode
    {
        /// <summary>No hook installed — nothing is captured.</summary>
        None,

        /// <summary>Emit only while inside OnChatMessageReceived. A redraw emits nothing.</summary>
        Receive,

        /// <summary>OnChatMessageReceived not found: emit unless inside GenerateChatFromHistory.</summary>
        SuppressHistory,

        /// <summary>Neither gate found: emit every render, backlog included. Servers must dedupe.</summary>
        Legacy,
    }

    // Harmony patches are static, so the gate state has to be too. All of this runs on the
    // Unity main thread (chat rendering is UI work), so plain ints are sufficient.
    private static Action<string, string, string>? s_onMessage;
    private static int s_receiveDepth;
    private static int s_replayDepth;
    private static CaptureMode s_mode = CaptureMode.None;

    private readonly ManualLogSource _log;
    private readonly Harmony _harmony;
    private bool _installed;

    /// <param name="onMessage">(userId, nick, message) for each newly received message.</param>
    public ChatCaptureService(ManualLogSource log, string harmonyId,
        Action<string, string, string> onMessage)
    {
        _log = log;
        _harmony = new Harmony(harmonyId + ".chat");
        s_onMessage = onMessage;
    }

    internal static CaptureMode Mode => s_mode;

    public void Install()
    {
        if (_installed) return;

        try
        {
            var type = AccessTools.TypeByName("Liftoff.Multiplayer.Chat.ChatWindowPanel");
            if (type == null)
            {
                _log.LogWarning("[Chat] ChatWindowPanel type not found — chat capture unavailable.");
                return;
            }

            var render = AccessTools.Method(type, "GenerateUserMessage",
                new[] { typeof(string), typeof(string), typeof(string), typeof(Color) });

            if (render == null)
            {
                _log.LogWarning("[Chat] GenerateUserMessage(string,string,string,Color) not found — chat capture unavailable.");
                return;
            }

            // The receive gate. Its parameter is an obfuscated message type, so match on name
            // only — ChatWindowPanel declares exactly one OnChatMessageReceived.
            var receive = AccessTools.Method(type, "OnChatMessageReceived");

            // The replay gate — the backlog redraw itself.
            var replay = AccessTools.Method(type, "GenerateChatFromHistory");

            if (receive != null)
            {
                _harmony.Patch(receive,
                    prefix: new HarmonyMethod(typeof(ChatCaptureService), nameof(ReceiveEnter)),
                    finalizer: new HarmonyMethod(typeof(ChatCaptureService), nameof(ReceiveExit)));
                s_mode = CaptureMode.Receive;
            }

            if (replay != null)
            {
                _harmony.Patch(replay,
                    prefix: new HarmonyMethod(typeof(ChatCaptureService), nameof(ReplayEnter)),
                    finalizer: new HarmonyMethod(typeof(ChatCaptureService), nameof(ReplayExit)));
                if (s_mode == CaptureMode.None) s_mode = CaptureMode.SuppressHistory;
            }

            if (s_mode == CaptureMode.None)
            {
                s_mode = CaptureMode.Legacy;
                _log.LogWarning(
                    "[Chat] Neither OnChatMessageReceived nor GenerateChatFromHistory found — " +
                    "falling back to emitting every rendered message. The retained backlog will be " +
                    "re-emitted on each race; servers must dedupe on (session_id, chat_id).");
            }

            _harmony.Patch(render,
                postfix: new HarmonyMethod(typeof(ChatCaptureService), nameof(RenderPostfix)));

            _installed = true;
            _log.LogInfo($"[Chat] Chat capture installed. Mode={s_mode}.");
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Chat] Failed to install chat capture: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        s_onMessage = null;
        if (_installed)
        {
            _harmony.UnpatchSelf();
            _installed = false;
        }
        s_receiveDepth = 0;
        s_replayDepth = 0;
        s_mode = CaptureMode.None;
    }

    // Finalizers (rather than postfixes) so the depth is restored even if the patched
    // method throws — a leaked depth would silently disable or unblock capture for good.
    private static void ReceiveEnter() => s_receiveDepth++;
    private static void ReceiveExit() { if (s_receiveDepth > 0) s_receiveDepth--; }
    private static void ReplayEnter() => s_replayDepth++;
    private static void ReplayExit() { if (s_replayDepth > 0) s_replayDepth--; }

    // Harmony injects positional args as __0, __1, __2 regardless of compiled param names.
    private static void RenderPostfix(string __0, string __1, string __2)
    {
        try
        {
            switch (s_mode)
            {
                case CaptureMode.Receive:
                    // Only a live receive counts. Backlog redraws happen outside it.
                    if (s_receiveDepth <= 0 || s_replayDepth > 0) return;
                    break;

                case CaptureMode.SuppressHistory:
                    if (s_replayDepth > 0) return;
                    break;

                case CaptureMode.Legacy:
                    break;

                default:
                    return;
            }

            s_onMessage?.Invoke(__0, __1, __2);
        }
        catch { }
    }
}
