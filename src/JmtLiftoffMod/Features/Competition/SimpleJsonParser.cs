using System;
using System.Collections.Generic;

namespace JmtLiftoffMod.Features.Competition;

/// <summary>
/// Minimal JSON parser for flat string-valued objects received as server commands.
/// Handles only the subset the competition server sends — no nesting, no arrays.
/// </summary>
internal static class SimpleJsonParser
{
    /// <summary>
    /// Parses a JSON object string into a flat string dictionary.
    /// Returns null if the input is not a valid JSON object.
    /// String values are unescaped; numbers and booleans are returned as their raw text.
    /// </summary>
    public static Dictionary<string, string>? TryParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        var s = json.Trim();
        if (!s.StartsWith("{") || !s.EndsWith("}")) return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = s.Substring(1, s.Length - 2).Trim();
        if (body.Length == 0) return result;

        var pos = 0;
        while (pos < body.Length)
        {
            // Skip whitespace and commas
            while (pos < body.Length && (body[pos] == ',' || body[pos] == ' ' || body[pos] == '\r' || body[pos] == '\n' || body[pos] == '\t'))
                pos++;

            if (pos >= body.Length) break;

            // Read key (must be a quoted string)
            if (body[pos] != '"') return null;
            var key = ReadString(body, ref pos);
            if (key == null) return null;

            // Skip colon
            while (pos < body.Length && body[pos] != ':') pos++;
            if (pos >= body.Length) return null;
            pos++; // skip ':'

            // Skip whitespace
            while (pos < body.Length && body[pos] == ' ') pos++;
            if (pos >= body.Length) return null;

            // Read value
            string? value;
            if (body[pos] == '"')
            {
                value = ReadString(body, ref pos);
            }
            else
            {
                // Number, bool, null — read until comma or end
                var start = pos;
                while (pos < body.Length && body[pos] != ',' && body[pos] != '}')
                    pos++;
                value = body.Substring(start, pos - start).Trim();
            }

            if (value != null)
                result[key] = value;
        }

        return result;
    }

    private static string? ReadString(string s, ref int pos)
    {
        if (pos >= s.Length || s[pos] != '"') return null;
        pos++; // skip opening quote

        var sb = new System.Text.StringBuilder();
        while (pos < s.Length)
        {
            var c = s[pos++];
            if (c == '"') return sb.ToString();
            if (c == '\\' && pos < s.Length)
            {
                var esc = s[pos++];
                switch (esc)
                {
                    case '"':  sb.Append('"');  break;
                    case '\\': sb.Append('\\'); break;
                    case '/':  sb.Append('/');  break;
                    case 'n':  sb.Append('\n'); break;
                    case 'r':  sb.Append('\r'); break;
                    case 't':  sb.Append('\t'); break;
                    default:   sb.Append(esc);  break;
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        return null; // unterminated string
    }
}
