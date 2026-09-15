using System;
using UnityEngine;

namespace JmtLiftoffMod.Features.Diagnostics;

/// <summary>
/// Hosts the mod's IMGUI windows (the stats overlay and the track-control debug panel).
///
/// Unity runs the IMGUI event loop for every enabled behaviour that has an OnGUI method,
/// several times a frame, whether or not it draws anything. The windows are hidden nearly
/// always, so they live on this component, which the plugin enables only while one is shown.
/// </summary>
internal sealed class DebugGui : MonoBehaviour
{
    public Action? Draw;

    private void OnGUI()
    {
        using var probe = FrameProbe.Measure(FrameProbe.Part.Gui);
        Draw?.Invoke();
    }
}
