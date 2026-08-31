using System;
using System.Collections.Generic;
using System.Linq;

namespace JmtLiftoffMod.Features.MultiplayerTrackControl;

internal static class MultiplayerTargetSequence
{
    public static List<SequenceEntry> Parse(string? text, MultiplayerTrackControlLog log)
    {
        var entries = new List<SequenceEntry>();
        if (string.IsNullOrWhiteSpace(text))
            return entries;

        var sourceText = text!;
        var rawEntries = sourceText
            .Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();

        for (var index = 0; index < rawEntries.Count; index++)
        {
            var rawEntry = rawEntries[index];
            var parts = rawEntry.Split('|').Select(part => part.Trim()).ToArray();
            if (parts.Length < 2)
            {
                log.Warn("SEQUENCE", $"Skipping malformed target sequence entry #{index + 1}: \"{rawEntry}\"");
                continue;
            }

            var environment = parts[0];
            var track = parts.Length > 1 ? parts[1] : string.Empty;
            var race = parts.Length > 2 ? parts[2] : track;
            var workshopId = parts.Length > 3 ? parts[3] : string.Empty;

            entries.Add(new SequenceEntry(environment, track, race, workshopId));
        }

        return entries;
    }

    public static bool TrySelectNext(
        IReadOnlyList<SequenceEntry> entries,
        string currentEnvironment,
        string currentTrack,
        string currentRace,
        bool loop,
        out SequenceEntry next)
    {
        next = default!;
        if (entries.Count == 0)
            return false;

        var currentIndex = entries
            .Select((entry, index) => new { entry, index })
            .FirstOrDefault(candidate =>
                Matches(candidate.entry.EnvironmentName, currentEnvironment) &&
                Matches(candidate.entry.TrackName, currentTrack) &&
                Matches(candidate.entry.RaceName, currentRace))
            ?.index ?? -1;

        var nextIndex = currentIndex + 1;
        if (nextIndex >= entries.Count)
        {
            if (!loop)
                return false;

            nextIndex = 0;
        }

        if (nextIndex < 0 || nextIndex >= entries.Count)
            return false;

        next = entries[nextIndex];
        return true;
    }

    private static bool Matches(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
            return true;

        return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class SequenceEntry
    {
        public SequenceEntry(string environmentName, string trackName, string raceName, string workshopId)
        {
            EnvironmentName = environmentName ?? string.Empty;
            TrackName = trackName ?? string.Empty;
            RaceName = raceName ?? string.Empty;
            WorkshopId = workshopId ?? string.Empty;
        }

        public string EnvironmentName { get; }
        public string TrackName { get; }
        public string RaceName { get; }
        public string WorkshopId { get; }

        public override string ToString()
        {
            return $"{EnvironmentName} | {TrackName} | {RaceName} | {WorkshopId}";
        }
    }
}
