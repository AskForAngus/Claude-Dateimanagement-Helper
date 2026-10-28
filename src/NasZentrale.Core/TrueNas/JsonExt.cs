using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NasZentrale.Core.TrueNas;

/// <summary>Tolerantes Auslesen von JSON-Werten – TrueNAS ändert Feldtypen gelegentlich zwischen Versionen.</summary>
public static class JsonExt
{
    public static string? Str(this JsonNode? node, string key)
    {
        var v = node?[key];
        return v switch
        {
            null => null,
            JsonValue jv when jv.TryGetValue<string>(out var s) => s,
            JsonObject o when o["value"] != null => o["value"]!.ToString(),
            _ => v.ToString(),
        };
    }

    public static long? Long(this JsonNode? node, string key)
    {
        var v = node?[key];
        if (v is JsonObject o) v = o["parsed"] ?? o["value"];
        if (v is not JsonValue jv) return null;
        if (jv.TryGetValue<long>(out var l)) return l;
        if (jv.TryGetValue<double>(out var d)) return (long)d;
        if (jv.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
        return null;
    }

    public static bool Bool(this JsonNode? node, string key, bool fallback = false)
    {
        var v = node?[key];
        if (v is JsonObject o) v = o["parsed"] ?? o["value"];
        if (v is not JsonValue jv) return fallback;
        if (jv.TryGetValue<bool>(out var b)) return b;
        if (jv.TryGetValue<string>(out var s)) return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "on";
        return fallback;
    }

    public static IEnumerable<JsonObject> Objects(this JsonNode? node) =>
        node is JsonArray a ? a.OfType<JsonObject>() : Enumerable.Empty<JsonObject>();

    /// <summary>Zeitstempel in TrueNAS-Form: {"$date": ms} oder Sekunden als Zahl.</summary>
    public static DateTimeOffset? Date(this JsonNode? node, string key)
    {
        var v = node?[key];
        if (v is JsonObject o && o["$date"] is JsonValue d && d.TryGetValue<long>(out var ms))
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        if (v is JsonValue jv && jv.TryGetValue<double>(out var secs))
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(secs * 1000));
        return null;
    }

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
