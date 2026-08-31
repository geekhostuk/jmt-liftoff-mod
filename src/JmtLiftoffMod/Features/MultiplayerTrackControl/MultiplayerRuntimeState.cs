using System;

namespace JmtLiftoffMod.Features.MultiplayerTrackControl;

internal static class MultiplayerRuntimeState
{
    private static readonly object SyncRoot = new();
    private static object? s_latestGameSettings;
    private static string s_latestGameSettingsSource = string.Empty;
    private static DateTime s_latestGameSettingsUtc = DateTime.MinValue;
    private static object? s_latestPopupWithSetGameCallback;
    private static Delegate? s_latestSetGameCallback;
    private static string s_latestPopupCallbackSource = string.Empty;
    private static DateTime s_latestPopupCallbackUtc = DateTime.MinValue;

    public static void RecordGameSettingsSnapshot(object? value, string source)
    {
        if (value == null)
            return;

        lock (SyncRoot)
        {
            s_latestGameSettings = value;
            s_latestGameSettingsSource = source;
            s_latestGameSettingsUtc = DateTime.UtcNow;
        }
    }

    public static bool TryGetLatestGameSettingsSnapshot(out object? value, out string source, out DateTime capturedUtc)
    {
        lock (SyncRoot)
        {
            value = s_latestGameSettings;
            source = s_latestGameSettingsSource;
            capturedUtc = s_latestGameSettingsUtc;
            return value != null;
        }
    }

    public static void RecordPopupWithSetGameCallback(object? popup, Delegate? callback, string source)
    {
        if (popup == null && callback == null)
            return;

        lock (SyncRoot)
        {
            s_latestPopupWithSetGameCallback = popup;
            s_latestSetGameCallback = callback;
            s_latestPopupCallbackSource = source;
            s_latestPopupCallbackUtc = DateTime.UtcNow;
        }
    }

    public static bool TryGetLatestPopupWithSetGameCallback(out object? popup, out Delegate? callback, out string source, out DateTime capturedUtc)
    {
        lock (SyncRoot)
        {
            popup = s_latestPopupWithSetGameCallback;
            callback = s_latestSetGameCallback;
            source = s_latestPopupCallbackSource;
            capturedUtc = s_latestPopupCallbackUtc;
            return popup != null || callback != null;
        }
    }

    public static void ClearLatestPopupWithSetGameCallback()
    {
        lock (SyncRoot)
        {
            s_latestPopupWithSetGameCallback = null;
            s_latestSetGameCallback = null;
            s_latestPopupCallbackSource = string.Empty;
            s_latestPopupCallbackUtc = DateTime.MinValue;
        }
    }

    public static void ClearLatestPopupObject()
    {
        lock (SyncRoot)
        {
            s_latestPopupWithSetGameCallback = null;
        }
    }
}
