using System.Collections.Generic;

namespace JmtLiftoffMod.Features.MultiplayerTrackControl;

/// <summary>
/// Narrow interface for track control operations.
/// Hides the reflection-heavy implementation behind a testable boundary.
/// </summary>
internal interface ITrackControlAdapter
{
    MultiplayerTrackChangeExecutionStatus AttemptConfiguredChange();
    MultiplayerTrackChangeExecutionStatus AttemptCycleNext();
    bool TryCatalogSnapshot(out Dictionary<string, object?> catalog);
    void DumpCurrentState();
}
