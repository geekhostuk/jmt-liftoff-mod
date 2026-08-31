using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JmtLiftoffMod.Features.Racing;

/// <summary>
/// Handles JSON serialization and dispatching of race events
/// to both the file logger and the competition server.
/// </summary>
internal static class JsonSerializer
{
    public static string SerializeJsonObject(IDictionary<string, object?> map)
    {
        var sb = new StringBuilder(256);
        sb.Append('{');
        var first = true;
        foreach (var kv in map)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(JsonEscape(kv.Key)).Append('"').Append(':').Append(SerializeJsonValue(kv.Value));
        }
        sb.Append('}');
        return sb.ToString();
    }

    public static string SerializeJsonValue(object? value)
    {
        if (value == null) return "null";

        switch (value)
        {
            case string str:
                return "\"" + JsonEscape(str) + "\"";
            case bool bo:
                return bo ? "true" : "false";
            case byte b:
                return b.ToString(CultureInfo.InvariantCulture);
            case short s:
                return s.ToString(CultureInfo.InvariantCulture);
            case int i:
                return i.ToString(CultureInfo.InvariantCulture);
            case long l:
                return l.ToString(CultureInfo.InvariantCulture);
            case float f:
                return f.ToString("0.###", CultureInfo.InvariantCulture);
            case double d:
                return d.ToString("0.###", CultureInfo.InvariantCulture);
            case decimal m:
                return m.ToString(CultureInfo.InvariantCulture);
            case IEnumerable<int> ints:
                return "[" + string.Join(",", ints.Select(x => x.ToString(CultureInfo.InvariantCulture))) + "]";
            case IEnumerable<string> strings:
                return "[" + string.Join(",", strings.Select(x => "\"" + JsonEscape(x) + "\"")) + "]";
            case IDictionary<string, object?> dict:
                return SerializeJsonObject(dict);
            case System.Collections.IEnumerable enumerable:
            {
                var parts = new List<string>();
                foreach (var item in enumerable)
                    parts.Add(SerializeJsonValue(item));
                return "[" + string.Join(",", parts) + "]";
            }
            default:
                return "\"" + JsonEscape(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty) + "\"";
        }
    }

    public static string JsonEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }
}
