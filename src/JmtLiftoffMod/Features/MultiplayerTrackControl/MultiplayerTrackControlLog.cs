using System;
using BepInEx.Logging;

namespace JmtLiftoffMod.Features.MultiplayerTrackControl;

internal sealed class MultiplayerTrackControlLog
{
    private readonly ManualLogSource _logger;
    private readonly Action<string> _stateLog;
    private readonly Func<bool>? _stateLogEnabled;

    public MultiplayerTrackControlLog(ManualLogSource logger, Action<string> stateLog, Func<bool>? stateLogEnabled = null)
    {
        _logger = logger;
        _stateLog = stateLog;
        _stateLogEnabled = stateLogEnabled;
    }

    public void Info(string category, string message)
    {
        _stateLog(Format(category, message));
    }

    /// <summary>
    /// Lazy Info: the message is only built when state logging is enabled. Info writes
    /// only to the state log, so when it is disabled there is nothing to emit — and the
    /// (often expensive, reflection-based) message factory should not run at all. Used on
    /// the high-frequency Photon property-update paths to avoid per-event main-thread work.
    /// </summary>
    public void Info(string category, Func<string> messageFactory)
    {
        if (_stateLogEnabled != null && !_stateLogEnabled())
            return;
        _stateLog(Format(category, messageFactory()));
    }

    public void Warn(string category, string message)
    {
        var line = Format(category, message);
        _logger.LogWarning(line);
        _stateLog(line);
    }

    public void Error(string category, string message, Exception? exception = null)
    {
        var line = Format(category, message);
        _logger.LogError(exception == null ? line : $"{line}{Environment.NewLine}{exception}");
        _stateLog(line);
        if (exception != null)
            _stateLog(Format(category, exception.ToString()));
    }

    private static string Format(string category, string message)
    {
        return $"[MTC][{category}] {message}";
    }
}
