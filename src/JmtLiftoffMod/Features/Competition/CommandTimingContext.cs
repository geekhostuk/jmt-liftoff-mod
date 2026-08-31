using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace JmtLiftoffMod.Features.Competition;

/// <summary>
/// Lightweight phase-level timing for plugin commands (set_track, prepare_track).
/// Created on the WebSocket receive thread; flows through the execution path via optional params.
/// </summary>
internal sealed class CommandTimingContext
{
    private readonly Stopwatch _overall = Stopwatch.StartNew();
    private readonly Dictionary<string, long> _phases = new();
    private readonly string _commandId;
    private readonly string _commandName;

    private Stopwatch? _currentPhase;
    private string? _currentPhaseName;
    private long _queueDelayMs;
    private string? _diagnostics;

    public CommandTimingContext(string? commandId, string commandName)
    {
        _commandId = commandId ?? "";
        _commandName = commandName;
    }

    public long QueueDelayMs => _queueDelayMs;

    /// <summary>
    /// Attach execution diagnostics summary to include in the ACK payload.
    /// </summary>
    public void SetDiagnostics(string diagnostics) => _diagnostics = diagnostics;

    /// <summary>
    /// Call at the start of the RunOnMainThread lambda to record how long the action
    /// sat in the Unity SynchronizationContext queue.
    /// </summary>
    public void MarkMainThreadPickup()
    {
        _queueDelayMs = _overall.ElapsedMilliseconds;
    }

    /// <summary>
    /// Ends any active phase and starts a new named phase.
    /// </summary>
    public void StartPhase(string name)
    {
        EndCurrentPhase();
        _currentPhaseName = name;
        _currentPhase = Stopwatch.StartNew();
    }

    /// <summary>
    /// Ends the current phase without starting a new one.
    /// </summary>
    public void EndCurrentPhase()
    {
        if (_currentPhase != null && _currentPhaseName != null)
        {
            _currentPhase.Stop();
            _phases[_currentPhaseName] = _currentPhase.ElapsedMilliseconds;
        }

        _currentPhase = null;
        _currentPhaseName = null;
    }

    /// <summary>
    /// Human-readable summary for BepInEx log output.
    /// Example: "total=76ms queue=12ms discovery_refresh=2;host_capture=1;popup_acquire=45"
    /// </summary>
    public string GetSummary()
    {
        EndCurrentPhase();
        var sb = new StringBuilder();
        sb.Append("total=").Append(_overall.ElapsedMilliseconds).Append("ms");
        sb.Append(" queue=").Append(_queueDelayMs).Append("ms");
        if (_phases.Count > 0)
        {
            sb.Append(' ');
            var first = true;
            foreach (var kvp in _phases)
            {
                if (!first) sb.Append(';');
                sb.Append(kvp.Key).Append('=').Append(kvp.Value);
                first = false;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns JSON fragment fields to embed in the command_ack payload.
    /// </summary>
    public string ToAckJsonFields()
    {
        EndCurrentPhase();
        var phasesStr = new StringBuilder();
        var first = true;
        foreach (var kvp in _phases)
        {
            if (!first) phasesStr.Append(';');
            phasesStr.Append(kvp.Key).Append('=').Append(kvp.Value);
            first = false;
        }

        var diagField = string.IsNullOrEmpty(_diagnostics) ? "" : $",\"diagnostics\":\"{EscapeJsonString(_diagnostics!)}\"";
        return $"\"timing_total_ms\":{_overall.ElapsedMilliseconds},\"timing_queue_ms\":{_queueDelayMs},\"timing_phases\":\"{phasesStr}\"{diagField}";
    }

    private static string EscapeJsonString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
