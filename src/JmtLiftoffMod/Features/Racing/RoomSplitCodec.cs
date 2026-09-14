using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace JmtLiftoffMod.Features.Racing;

/// <summary>One lap's gate times, as a pilot's leaderboard plugin published them in the room.</summary>
internal sealed class RoomSplit
{
    public RoomSplit(int format, int seq, int lapMs, int? prevLapMs, string[] gates, int[] times)
    {
        Format = format;
        Seq = seq;
        LapMs = lapMs;
        PrevLapMs = prevLapMs;
        Gates = gates;
        Times = times;
    }

    public int Format { get; }
    public int Seq { get; }
    /// <summary>The lap's time as the plugin measured it: what the gate times add up to.</summary>
    public int LapMs { get; }
    /// <summary>The lap before it in the pilot's run, as the plugin measured it.</summary>
    public int? PrevLapMs { get; }
    /// <summary>The checkpoints' ids, in the order the lap passed them.</summary>
    public string[] Gates { get; }
    /// <summary>Milliseconds into the lap at each gate.</summary>
    public int[] Times { get; }
}

/// <summary>
/// Reads the gate splits the JMT Liftoff Leaderboard plugin publishes on its pilot's Photon
/// player, one <c>SetCustomProperties</c> call per lap:
/// <list type="bullet">
/// <item><c>JMTG</c>: <c>string[]</c>, the lap's gate ids in the order flown. Sent only when
/// they differ from the last ones published, so an update without it means the gates held on
/// the player are still the lap's.</item>
/// <item><c>JMTS</c>: <c>int[]</c> {format, seq, gatesHash, lap_ms, prev_lap_ms (0 = none),
/// t1..tn}, one time for each gate.</item>
/// </list>
/// <c>gatesHash</c> is FNV-1a 32 over the UTF-8 of the ids joined by <c>\n</c>, so the times
/// are never paired with gates they weren't measured through. The plugin writes the same
/// layout; both check the vectors ["a","b"] → 0x28E4C710 and the three gates in
/// docs/server-protocol.md → 0x1FCDE9CE.
///
/// Pure, so it can be checked outside the game. Anyone in a room can set these properties, so
/// nothing is taken on trust: a split that isn't a lap is refused here.
/// </summary>
internal static class RoomSplitCodec
{
    public const string GatesKey = "JMTG";
    public const string SplitsKey = "JMTS";
    public const int Format = 1;
    /// <summary>format, seq, gatesHash, lap_ms, prev_lap_ms: the times follow.</summary>
    public const int HeaderLength = 5;
    /// <summary>More than any course has.</summary>
    public const int MaxGates = 200;
    /// <summary>Quicker than this between gates is one passage reported twice, or made up.</summary>
    public const int MinStretchMs = 40;
    public const int MaxLapMs = 3_600_000;

    private static readonly Regex GateId = new("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant);

    public static int GatesHash(IReadOnlyList<string> gates)
    {
        unchecked
        {
            var hash = 0x811C9DC5u;
            foreach (var b in Encoding.UTF8.GetBytes(string.Join("\n", gates)))
            {
                hash ^= b;
                hash *= 16777619u;
            }
            return (int)hash;
        }
    }

    /// <summary>The <c>JMTS</c> array for a lap: what the plugin publishes.</summary>
    public static int[] Encode(int seq, IReadOnlyList<string> gates, IReadOnlyList<int> times, int lapMs, int? prevLapMs)
    {
        var data = new int[HeaderLength + times.Count];
        data[0] = Format;
        data[1] = seq;
        data[2] = GatesHash(gates);
        data[3] = lapMs;
        data[4] = prevLapMs ?? 0;
        for (var i = 0; i < times.Count; i++)
            data[HeaderLength + i] = times[i];
        return data;
    }

    /// <summary>Just the sequence number, so a repeat is recognised before anything else is read.</summary>
    public static bool TryReadSeq(object? splitsValue, out int seq)
    {
        var data = IntsOf(splitsValue);
        seq = data != null && data.Length >= HeaderLength ? data[1] : 0;
        return data != null && data.Length >= HeaderLength;
    }

    public static bool TryDecode(object? splitsValue, object? gatesValue, out RoomSplit? split, out string why)
    {
        split = null;
        var data = IntsOf(splitsValue);
        if (data == null)
            return Refuse("JMTS is not an int array", out why);
        if (data.Length < HeaderLength)
            return Refuse($"JMTS has {data.Length} values, fewer than its header", out why);
        if (data[0] != Format)
            return Refuse($"JMTS format {data[0]} is not {Format}", out why);

        var gates = StringsOf(gatesValue);
        if (gates == null)
            return Refuse("no JMTG gates to go with the times", out why);
        var count = data.Length - HeaderLength;
        if (count != gates.Length)
            return Refuse($"{count} times for {gates.Length} gates", out why);
        if (count < 1 || count > MaxGates)
            return Refuse($"{count} gates is not a course's", out why);
        if (GatesHash(gates) != data[2])
            return Refuse("the gates held aren't the ones the times were measured through", out why);

        var lapMs = data[3];
        if (lapMs <= 0 || lapMs > MaxLapMs)
            return Refuse($"lap_ms {lapMs} is not a lap", out why);
        var prevLapMs = data[4];
        if (prevLapMs < 0 || prevLapMs > MaxLapMs)
            return Refuse($"prev_lap_ms {prevLapMs} is not a lap", out why);

        for (var i = 0; i < gates.Length; i++)
        {
            if (gates[i] == null || !GateId.IsMatch(gates[i]))
                return Refuse("a gate id is not a checkpoint's id", out why);
            if (i > 0 && gates[i] == gates[i - 1])
                return Refuse("the same gate twice in a row is one passage reported twice", out why);
        }

        var times = new int[count];
        Array.Copy(data, HeaderLength, times, 0, count);
        var previous = 0;
        for (var i = 0; i <= count; i++)
        {
            var at = i < count ? times[i] : lapMs;
            if (at - previous < MinStretchMs)
                return Refuse($"the times must rise through the lap, each stretch taking at least {MinStretchMs} ms", out why);
            previous = at;
        }

        split = new RoomSplit(data[0], data[1], lapMs, prevLapMs == 0 ? null : prevLapMs, gates, times);
        why = string.Empty;
        return true;
    }

    private static bool Refuse(string reason, out string why)
    {
        why = reason;
        return false;
    }

    private static int[]? IntsOf(object? value)
    {
        switch (value)
        {
            case int[] ints:
                return ints;
            case object[] items:
            {
                var ints = new int[items.Length];
                for (var i = 0; i < items.Length; i++)
                {
                    switch (items[i])
                    {
                        case int n: ints[i] = n; break;
                        case short n: ints[i] = n; break;
                        case byte n: ints[i] = n; break;
                        case long n when n is >= int.MinValue and <= int.MaxValue: ints[i] = (int)n; break;
                        default: return null;
                    }
                }
                return ints;
            }
            default:
                return null;
        }
    }

    private static string[]? StringsOf(object? value)
    {
        switch (value)
        {
            case string[] strings:
                return strings;
            case object[] items:
            {
                var strings = new string[items.Length];
                for (var i = 0; i < items.Length; i++)
                {
                    if (items[i] is not string s)
                        return null;
                    strings[i] = s;
                }
                return strings;
            }
            default:
                return null;
        }
    }
}
