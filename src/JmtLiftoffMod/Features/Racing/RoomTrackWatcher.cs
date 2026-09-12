using System;
using JmtLiftoffMod.Features.MultiplayerTrackControl;

namespace JmtLiftoffMod.Features.Racing;

/// <summary>
/// What the room says is being flown, from its Photon properties: the environment
/// (<c>E</c>), the track and race (<c>T</c>, <c>R</c>) and the Workshop id, when one
/// can be found (see <see cref="WorkshopIdOf"/>).
/// </summary>
public sealed class RoomTrack : IEquatable<RoomTrack>
{
    public RoomTrack(string env, string track, string race, string workshopId)
    {
        Env = env;
        Track = track;
        Race = race;
        WorkshopId = workshopId;
    }

    public string Env { get; }
    public string Track { get; }
    public string Race { get; }
    public string WorkshopId { get; }

    /// <summary>
    /// The Workshop id of what the room is flying, or empty when there is none to find.
    /// The room's <c>R</c> and <c>T</c> are <c>Liftoff.Multiplayer.GameContentEntry</c>,
    /// whose <c>ManagedID</c> string is the <c>managedID</c> of the .race or .track file:
    /// the Workshop id. The game sets no room property for it. The race is asked first,
    /// because a Workshop course is loaded as its Race item; then the track; then
    /// <c>W</c>, for a room that does carry one. A <c>ManagedID</c> holding a <c>str</c>,
    /// the shape of the game's other content objects, is read too. Only digits count:
    /// Liftoff's own tracks have no Workshop id, and a wrong one would file laps under
    /// another course.
    /// </summary>
    public static string WorkshopIdOf(object? race, object? track, object? roomWorkshopId)
    {
        foreach (var content in new[] { race, track })
        {
            var managed = ReflectionHelper.GetMemberValue(content, "ManagedID");
            var id = managed as string
                     ?? (ReflectionHelper.TryGetNestedString(managed, out var nested, "str") ? nested : "");
            if (IsWorkshopId(id))
                return id;
        }

        var w = roomWorkshopId?.ToString() ?? "";
        return IsWorkshopId(w) ? w : "";
    }

    private static bool IsWorkshopId(string value)
    {
        foreach (var c in value)
        {
            if (c < '0' || c > '9')
                return false;
        }
        return value.Length > 0;
    }

    public bool Equals(RoomTrack? other) =>
        other != null && Env == other.Env && Track == other.Track
        && Race == other.Race && WorkshopId == other.WorkshopId;

    public override bool Equals(object? obj) => Equals(obj as RoomTrack);

    public override int GetHashCode() => (Env, Track, Race, WorkshopId).GetHashCode();

    public override string ToString() =>
        $"{Env}/{Track} ({Race})" + (WorkshopId.Length > 0 ? $" #{WorkshopId}" : "");
}

public enum RoomTrackChange
{
    None,
    /// <summary>The change a track command asked for. Its race started when the command arrived.</summary>
    Commanded,
    /// <summary>A change nobody asked the plugin for: the host picked another track in game.</summary>
    InGame,
}

/// <summary>
/// Notices the room moving to another track, however it got there.
///
/// A <c>set_track</c> or <c>next_track</c> starts its race as it arrives, before the game
/// has loaded anything. A track picked in game starts none, so without this its laps run
/// on under the previous track's race. Kept free of Unity and Photon so it can be tested
/// on its own: the plugin reads the room and passes it in.
/// </summary>
public sealed class RoomTrackWatcher
{
    /// <summary>
    /// How long a new reading must hold before it counts. The room's properties can change
    /// over more than one update, and half of a change is not a track.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long after a track command the room's change is taken to be that command's. A
    /// command that reloads the track already loaded changes nothing, so the window closes
    /// on its own rather than waiting for a change that is not coming.
    /// </summary>
    public static readonly TimeSpan CommandWindow = TimeSpan.FromSeconds(60);

    private RoomTrack? _candidate;
    private DateTime _candidateSince;
    private DateTime _expectedUntil = DateTime.MinValue;

    /// <summary>The track the room was last settled on, or null before the first reading.</summary>
    public RoomTrack? Current { get; private set; }

    /// <summary>A track command has just been carried out: its change is coming.</summary>
    public void ExpectChange(DateTime now) => _expectedUntil = now + CommandWindow;

    /// <summary>
    /// One reading of the room, null when not in one. Returns what, if anything, changed;
    /// <see cref="Current"/> is then the track the room moved to.
    /// </summary>
    public RoomTrackChange Observe(RoomTrack? room, DateTime now)
    {
        // Out of a room, or a room that has not named its track: nothing to compare. The
        // track last seen is kept, so coming back to a different one still counts.
        if (room == null || room.Track.Length == 0)
        {
            _candidate = null;
            return RoomTrackChange.None;
        }

        // The first reading is where the room already was, not a change.
        if (Current == null)
        {
            Current = room;
            return RoomTrackChange.None;
        }

        if (room.Equals(Current))
        {
            _candidate = null;
            return RoomTrackChange.None;
        }

        if (!room.Equals(_candidate))
        {
            _candidate = room;
            _candidateSince = now;
            return RoomTrackChange.None;
        }

        if (now - _candidateSince < Settle)
            return RoomTrackChange.None;

        Current = room;
        _candidate = null;
        if (now < _expectedUntil)
        {
            _expectedUntil = DateTime.MinValue;
            return RoomTrackChange.Commanded;
        }
        return RoomTrackChange.InGame;
    }
}
