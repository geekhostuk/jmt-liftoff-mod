using System;
using System.IO;
using BepInEx.Configuration;

namespace JmtLiftoffMod;

/// <summary>
/// Builds an in-memory <see cref="ConfigFile"/> for settings that should NOT appear in the
/// user-facing plugin config. Entries bound to it behave exactly like normal config entries
/// (read/write Value, one-shot command resets, SettingChanged, Definition.Key) but are never
/// persisted, so the visible .cfg only ever contains the options we explicitly bind to
/// <c>Plugin.Config</c> (the essential connection settings).
/// </summary>
internal static class HiddenConfig
{
    /// <summary>
    /// Creates a throwaway <see cref="ConfigFile"/> that is never written to disk:
    /// the path is unique per run and never exists, <c>saveOnInit</c> is false, and
    /// <see cref="ConfigFile.SaveOnConfigSet"/> is disabled so writing a Value never flushes.
    /// </summary>
    public static ConfigFile Create()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jmt-liftoff-mod-defaults-{Guid.NewGuid():N}.cfg");
        return new ConfigFile(path, saveOnInit: false) { SaveOnConfigSet = false };
    }
}
