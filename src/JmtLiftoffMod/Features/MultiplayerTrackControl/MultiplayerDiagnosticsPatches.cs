using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace JmtLiftoffMod.Features.MultiplayerTrackControl;

internal sealed class MultiplayerDiagnosticsPatches : IDisposable
{
    private static MultiplayerTrackControlLog? s_log;
    private static Func<object?, string>? s_describe;

    private readonly Harmony _harmony;
    private readonly MultiplayerDiscoveryService _discovery;
    private bool _installed;

    public MultiplayerDiagnosticsPatches(
        string harmonyId,
        MultiplayerDiscoveryService discovery,
        MultiplayerTrackControlLog log,
        Func<object?, string> describe)
    {
        _harmony = new Harmony(harmonyId);
        _discovery = discovery;
        s_log = log;
        s_describe = describe;
    }

    public void Install()
    {
        if (_installed)
            return;

        _discovery.Refresh();

        PatchMethod("Liftoff.Multiplayer.GameSetup.PopupQuickPlayMultiplayerSetup", "ApplyFromGameSettings", 1);
        PatchMethod("Liftoff.Multiplayer.GameSetup.PopupQuickPlayMultiplayerSetup", "ApplyToGameSettings", 1);
        PatchMethod("Liftoff.Multiplayer.GameSetup.PopupQuickPlayMultiplayerSetup", "OnSetGame", 0);
        PatchMethod("Liftoff.Multiplayer.GameSetup.PopupQuickPlayMultiplayerSetup", "add_onSetGame", 1);
        PatchMethod("Liftoff.Multiplayer.GameSetup.PopupQuickPlayMultiplayerSetup", "remove_onSetGame", 1);
        PatchMethod("Liftoff.Multiplayer.GameSetup.ContentSettingsPanel", "ApplyFromGameSettings", 1);
        PatchMethod("Liftoff.Multiplayer.GameSetup.ContentSettingsPanel", "ApplyToGameSettings", 1);
        PatchMethod("Liftoff.Multiplayer.GameSetup.RoomSettingsPanel", "ApplyFromGameSettings", 1);
        PatchMethod("Liftoff.Multiplayer.GameSetup.RoomSettingsPanel", "ApplyToGameSettings", 1);
        PatchMethod("MultiplayerRaceScoreButtonPanel", "OnGameSettings", 0);
        PatchMethod("MultiplayerRaceScoreButtonPanel", "LoadGameSettings", 1);
        PatchMethod("MultiplayerRaceScoreButtonPanel", "OnGameSettingsUpdated", 2);
        PatchMethod("InGameMenuMainPanel", "OnMultiplayerGameSettings", 0);
        PatchMethod("InGameMenuMainPanel", "LoadLevel", 1);
        PatchMethod("CurrentContentContainer", "UpdateGameSessionSettings", 1);
        PatchMethod("Liftoff.Multiplayer.Chat.ChatHistory", "AddMessage", 1, declaredOnly: true);
        PatchMethod("Liftoff.Multiplayer.Chat.ChatWindowPanel", "AddMessage", 1, declaredOnly: true);
        PatchMethod("Liftoff.Multiplayer.Chat.ChatWindowPanel", "RefreshMessages", 0, declaredOnly: true);
        PatchMethod("Liftoff.Multiplayer.Chat.ChatWindowPanel", "OnChatValueChange", 1, declaredOnly: true);
        PatchMethod("Liftoff.Multiplayer.Chat.ChatWindowPanel", "OnInputFieldEditEnded", 1, declaredOnly: true);
        PatchMethod("Liftoff.Multiplayer.Chat.ChatWindowPanel", "SendUserMessage", 0, declaredOnly: true);
        PatchMethod("Liftoff.Multiplayer.Chat.ChatWindowPanel", "OnChatMessageReceived", 1, declaredOnly: true);
        PatchMethod("Liftoff.Multiplayer.Chat.MessageEntryPanel", "OnSubmit", 0, declaredOnly: true);
        PatchMethod("Liftoff.Multiplayer.Chat.MessageEntryPanel", "OnSubmit", 1, declaredOnly: true);
        PatchMethod("Liftoff.Multiplayer.Chat.MessageEntryPanel", "SendMessage", 0, declaredOnly: true);
        PatchMethod("Liftoff.Multiplayer.Chat.MessageEntryPanel", "SendMessage", 1, declaredOnly: true);

        _installed = true;
    }

    public void Dispose()
    {
        if (!_installed)
            return;

        _harmony.UnpatchSelf();
        _installed = false;
    }

    private void PatchMethod(string typeName, string methodName, int parameterCount, bool declaredOnly = false)
    {
        var type = _discovery.TryResolveKnownType(typeName);
        var method = declaredOnly
            ? ReflectionHelper.FindDeclaredMethod(type, methodName, parameterCount)
            : ReflectionHelper.FindMethod(type, methodName, parameterCount);
        if (method == null)
        {
            s_log?.Warn("PATCH", $"Skipped patch for {typeName}.{methodName}({parameterCount})");
            return;
        }

        _harmony.Patch(
            method,
            prefix: new HarmonyMethod(typeof(MultiplayerDiagnosticsPatches), nameof(LogPrefix)),
            postfix: new HarmonyMethod(typeof(MultiplayerDiagnosticsPatches), nameof(LogPostfix)));

        s_log?.Info("PATCH", $"Patched {ReflectionHelper.FormatMethodSignature(method)}");
    }

    private static void LogPrefix(MethodBase __originalMethod, object? __instance, object[] __args)
    {
        var parts = new List<string>
        {
            $"method={__originalMethod.DeclaringType?.FullName}.{__originalMethod.Name}",
            $"instance={ReflectionHelper.DescribeObjectIdentity(__instance)}"
        };

        if (__args.Length > 0)
        {
            var source = $"{__originalMethod.DeclaringType?.FullName}.{__originalMethod.Name}";
            if (__originalMethod.Name is "ApplyFromGameSettings" or "UpdateGameSessionSettings")
                MultiplayerRuntimeState.RecordGameSettingsSnapshot(__args[0], source);
            else if (__originalMethod.DeclaringType?.FullName == "Liftoff.Multiplayer.GameSetup.PopupQuickPlayMultiplayerSetup" &&
                     __originalMethod.Name == "add_onSetGame")
                MultiplayerRuntimeState.RecordPopupWithSetGameCallback(__instance, __args[0] as Delegate, source);
            else if (__originalMethod.DeclaringType?.FullName == "Liftoff.Multiplayer.GameSetup.PopupQuickPlayMultiplayerSetup" &&
                     __originalMethod.Name == "remove_onSetGame")
                MultiplayerRuntimeState.ClearLatestPopupWithSetGameCallback();
        }

        if (__args.Length > 0 && s_describe != null)
        {
            var argText = string.Join("; ", __args.Select((argument, index) => $"arg{index}={ReflectionHelper.SafeDescribe(s_describe, argument)}"));
            parts.Add(argText);
        }

        s_log?.Info("PATCH", "ENTER " + string.Join(" | ", parts));
    }

    private static void LogPostfix(MethodBase __originalMethod, object? __instance)
    {
        s_log?.Info("PATCH", $"EXIT method={__originalMethod.DeclaringType?.FullName}.{__originalMethod.Name} instance={ReflectionHelper.DescribeObjectIdentity(__instance)}");
    }
}
