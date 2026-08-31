using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace JmtLiftoffMod.Features.Chat;

/// <summary>
/// Hooks into ChatWindowPanel.GenerateUserMessage to capture incoming chat messages.
/// The method signature is:
///   private void GenerateUserMessage(string userId, string userName, string message, Color ledColor)
/// It is called for every user chat message rendered in the chat window.
/// </summary>
internal sealed class ChatCaptureService : IDisposable
{
    private static Action<string, string, string>? s_onMessage;

    private readonly ManualLogSource _log;
    private readonly Harmony _harmony;
    private bool _installed;

    public ChatCaptureService(ManualLogSource log, string harmonyId, Action<string, string, string> onMessage)
    {
        _log = log;
        _harmony = new Harmony(harmonyId + ".chat");
        s_onMessage = onMessage;
    }

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

            var method = AccessTools.Method(type, "GenerateUserMessage",
                new[] { typeof(string), typeof(string), typeof(string), typeof(Color) });

            if (method == null)
            {
                _log.LogWarning("[Chat] GenerateUserMessage(string,string,string,Color) not found — chat capture unavailable.");
                return;
            }

            _harmony.Patch(method,
                postfix: new HarmonyMethod(typeof(ChatCaptureService), nameof(Postfix)));

            _installed = true;
            _log.LogInfo("[Chat] Chat capture installed.");
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
    }

    // Harmony injects positional args as __0, __1, __2 regardless of compiled param names.
    private static void Postfix(string __0, string __1, string __2)
    {
        try { s_onMessage?.Invoke(__0, __1, __2); }
        catch { }
    }
}
