using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;

namespace JmtLiftoffMod.Features.Racing;

/// <summary>
/// Reads the lap list out of a pilot's <c>GMS</c>, the object Liftoff publishes on their
/// Photon player. Its types and members are obfuscated and differ between race modes, so the
/// laps are found as a <c>float[]</c> of seconds wherever it sits.
///
/// This used to be done by writing the whole object out as text, the way the mod's logging
/// dumps it, and matching the text with a regex: a reflection walk and a string of it on the
/// main thread for every GMS update of every pilot in the room. This walks the object the
/// same way -- the same order, the same depth and the same caps on members and items -- so it
/// finds the list the text did, and reads each float directly. It reads them as the text
/// would have shown them in an English locale, so the lap times are the same to the
/// millisecond; in a locale that writes "12,345", the regex read that as 12 seconds.
/// </summary>
internal sealed class GmsLapReader
{
    // The logging dump's limits (JmtLiftoffMod.Describe). A list longer than MaxItems was
    // written with "..." in place of the rest, and so never matched.
    private const int MaxDepth = 6;
    private const int MaxItems = 40;

    private readonly Dictionary<Type, Func<object, object?>[]> _members = new();

    /// <summary>
    /// The longest acceptable lap list in <paramref name="gms"/>, as seconds, and whether it
    /// carries any <c>float[]</c> at all: GMS with none is the game republishing a pilot's
    /// state, empty, as it respawns their drone. A list is acceptable with 1 to
    /// <paramref name="lapCap"/> laps, each over a second and no longer than ten minutes.
    /// </summary>
    public bool TryRead(object gms, int lapCap, out List<float> lapTimesSec, out bool hasList)
    {
        float[]? best = null;
        var sawList = false;
        Visit(gms, 0);
        hasList = sawList;
        lapTimesSec = new List<float>();
        if (best == null)
            return false;
        foreach (var seconds in best)
            lapTimesSec.Add(AsWritten(seconds));
        return true;

        // Mirrors Describe(value, depth): each container passes depth + 1 to its contents.
        void Visit(object? value, int depth)
        {
            if (value == null)
                return;
            if (depth >= MaxDepth)
            {
                // Describe wrote "<Single[] depth-limit>": a list, but not one to read.
                if (value is float[])
                    sawList = true;
                return;
            }
            switch (value)
            {
                case byte or short or int or long or float or double or bool or string or byte[]:
                    return;
                case float[] list:
                    sawList = true;
                    // Describe wrote a list's items one level down, so a list just above the
                    // limit had its values written as "<Single depth-limit>": seen, not read.
                    if (depth + 1 < MaxDepth && Acceptable(list, lapCap) && (best == null || list.Length > best.Length))
                        best = list;
                    return;
                case PhotonHashtable table:
                    VisitEntries(table, depth + 1);
                    return;
                case IDictionary dictionary:
                    VisitEntries(dictionary, depth + 1);
                    return;
                case Array array:
                    for (var i = 0; i < Math.Min(array.Length, MaxItems); i++)
                        Visit(array.GetValue(i), depth + 1);
                    return;
                case ICollection collection:
                    var n = 0;
                    foreach (var item in collection)
                    {
                        if (++n > MaxItems)
                            break;
                        Visit(item, depth + 1);
                    }
                    return;
                default:
                    foreach (var read in Members(value.GetType()))
                        Visit(Get(read, value), depth + 1);
                    return;
            }
        }

        void VisitEntries(IDictionary entries, int depth)
        {
            var n = 0;
            foreach (DictionaryEntry entry in entries)
            {
                if (++n > MaxItems)
                    break;
                Visit(entry.Key, depth);
                Visit(entry.Value, depth);
            }
        }
    }

    private static bool Acceptable(float[] list, int lapCap)
    {
        if (list.Length == 0 || list.Length > MaxItems || list.Length > lapCap)
            return false;
        foreach (var seconds in list)
        {
            var value = AsWritten(seconds);
            // NaN fails both, as it failed the regex.
            if (!(value > 1f && value <= 600f))
                return false;
        }
        return true;
    }

    /// <summary>The float as the dump's text carried it: written out, then read back.</summary>
    private static float AsWritten(float seconds) =>
        float.Parse(seconds.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// A type's public readable properties, then its public fields, at most MaxItems in all:
    /// the members Describe wrote, in its order. Looked up once per type.
    /// </summary>
    private Func<object, object?>[] Members(Type type)
    {
        if (_members.TryGetValue(type, out var known))
            return known;
        var members = new List<Func<object, object?>>();
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (members.Count >= MaxItems)
                break;
            if (property.CanRead && property.GetIndexParameters().Length == 0)
                members.Add(target => property.GetValue(target, null));
        }
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            if (members.Count >= MaxItems)
                break;
            members.Add(target => field.GetValue(target));
        }
        return _members[type] = members.ToArray();
    }

    private static object? Get(Func<object, object?> read, object target)
    {
        try
        {
            return read(target);
        }
        catch
        {
            return null;
        }
    }
}
