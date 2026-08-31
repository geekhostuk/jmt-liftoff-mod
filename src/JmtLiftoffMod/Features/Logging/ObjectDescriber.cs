using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ExitGames.Client.Photon;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;

namespace JmtLiftoffMod.Features.Logging;

/// <summary>
/// Static utility for human-readable descriptions of arbitrary objects.
/// Primarily used for diagnostic logging of Photon events and properties.
/// </summary>
internal static class ObjectDescriber
{
    private const int MaxDepth = 6;
    private const int MaxCollectionItems = 40;
    private const int MaxBytePreview = 64;

    public static string Describe(object? value, int depth = 0)
    {
        if (value == null) return "<null>";
        if (depth >= MaxDepth) return $"<{value.GetType().Name} depth-limit>";

        switch (value)
        {
            case byte b:   return $"byte {b}";
            case short s:  return $"short {s}";
            case int i:    return $"int {i}";
            case long l:   return $"long {l}";
            case float f:  return $"float {f}";
            case double d: return $"double {d}";
            case bool bo:  return $"bool {bo}";
            case string str: return $"string \"{str}\"";
            case byte[] bytes:
                return $"byte[{bytes.Length}] {BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, MaxBytePreview))}{(bytes.Length > MaxBytePreview ? "..." : "")}";
            case PhotonHashtable ht:   return DescribeHashtable(ht, depth + 1);
            case IDictionary dict:     return DescribeDictionary(dict, depth + 1);
            case Array arr:            return DescribeArray(arr, depth + 1);
            case ICollection col when value is not string:
                return DescribeCollection(col, depth + 1);
            default:
                return DescribeObject(value, depth + 1);
        }
    }

    private static string DescribeArray(Array arr, int depth)
    {
        var limit = Math.Min(arr.Length, MaxCollectionItems);
        var parts = new List<string>(limit);
        for (var i = 0; i < limit; i++)
            parts.Add(Describe(arr.GetValue(i), depth));
        return $"{arr.GetType().Name}[{arr.Length}] [{string.Join(", ", parts)}]{(arr.Length > limit ? ", ..." : "")}";
    }

    private static string DescribeCollection(ICollection col, int depth)
    {
        var parts = new List<string>();
        var i = 0;
        foreach (var item in col)
        {
            if (++i > MaxCollectionItems) { parts.Add("..."); break; }
            parts.Add(Describe(item, depth));
        }
        return $"{col.GetType().Name} (Count={col.Count}) [{string.Join(", ", parts)}]";
    }

    private static string DescribeHashtable(PhotonHashtable ht, int depth)
    {
        var parts = new List<string>();
        var i = 0;
        foreach (DictionaryEntry de in ht)
        {
            if (++i > MaxCollectionItems) { parts.Add("..."); break; }
            parts.Add($"{Describe(de.Key, depth)}={Describe(de.Value, depth)}");
        }
        return $"Hashtable({ht.Count}) {{{string.Join(", ", parts)}}}";
    }

    private static string DescribeDictionary(IDictionary dict, int depth)
    {
        var parts = new List<string>();
        var i = 0;
        foreach (DictionaryEntry de in dict)
        {
            if (++i > MaxCollectionItems) { parts.Add("..."); break; }
            parts.Add($"{Describe(de.Key, depth)}={Describe(de.Value, depth)}");
        }
        return $"IDictionary({dict.Count}) {{{string.Join(", ", parts)}}}";
    }

    private static string DescribeObject(object value, int depth)
    {
        var type = value.GetType();
        var members = new List<string>();

        foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
            string formatted;
            try { formatted = Describe(prop.GetValue(value), depth); }
            catch (Exception ex) { formatted = $"<error:{ex.GetType().Name}>"; }
            members.Add($"{prop.Name}={formatted}");
            if (members.Count >= MaxCollectionItems) break;
        }

        if (members.Count < MaxCollectionItems)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                string formatted;
                try { formatted = Describe(field.GetValue(value), depth); }
                catch (Exception ex) { formatted = $"<error:{ex.GetType().Name}>"; }
                members.Add($"{field.Name}={formatted}");
                if (members.Count >= MaxCollectionItems) break;
            }
        }

        if (members.Count == 0)
            return $"{type.Name}: {value}";

        return $"{type.Name} {{{string.Join(", ", members)}}}";
    }
}
