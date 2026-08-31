using System;
using System.Collections.Generic;

namespace JmtLiftoffMod.Features.Diagnostics;

/// <summary>
/// Lightweight stats container updated by CompetitionClient.
/// Read on the Unity main thread by the BotStatsOverlay during OnGUI().
/// Fields are written from multiple threads; use volatile or interlocked where needed.
/// </summary>
internal sealed class CompetitionStats
{
    public volatile int OutboxDepth;
    public long TotalEventsSent;
    public long TotalEventsEnqueued;
    public long TotalEventsDropped;
    public volatile bool IsConnected;
    public DateTime LastSendUtc;
    public DateTime LastReceiveUtc;
    public volatile string LastCommand = "";

    private readonly object _lock = new();
    private readonly Dictionary<string, int> _eventCounts = new();
    private readonly Queue<string> _recentEvents = new();
    private const int MaxRecentEvents = 10;

    /// <summary>Records an event by extracting event_type from the JSON line.</summary>
    public void RecordEvent(string jsonLine)
    {
        // Quick extract of event_type value without full JSON parsing
        var eventType = ExtractEventType(jsonLine);
        if (string.IsNullOrEmpty(eventType)) return;

        lock (_lock)
        {
            _eventCounts.TryGetValue(eventType, out var count);
            _eventCounts[eventType] = count + 1;

            var entry = $"{DateTime.UtcNow:HH:mm:ss} {eventType}";
            _recentEvents.Enqueue(entry);
            while (_recentEvents.Count > MaxRecentEvents)
                _recentEvents.Dequeue();
        }
    }

    /// <summary>Returns a snapshot of event counts for display.</summary>
    public Dictionary<string, int> GetEventCountsSnapshot()
    {
        lock (_lock)
            return new Dictionary<string, int>(_eventCounts);
    }

    /// <summary>Returns a snapshot of recent events for display.</summary>
    public string[] GetRecentEventsSnapshot()
    {
        lock (_lock)
            return _recentEvents.ToArray();
    }

    private static string ExtractEventType(string json)
    {
        // Simple substring search — avoids full JSON parse overhead
        const string key = "\"event_type\":\"";
        var idx = json.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return "";
        var start = idx + key.Length;
        var end = json.IndexOf('"', start);
        return end > start ? json.Substring(start, end - start) : "";
    }
}
