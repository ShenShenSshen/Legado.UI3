using System.Text.Json;
using System.Text.Json.Nodes;

namespace Legado.UI3.Core;

public static class J
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string S(this JsonNode? n, string key, string fallback = "") => n is JsonObject obj && obj[key] is JsonValue v
        ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : fallback;
    public static int I(this JsonNode? n, string key, int fallback = 0) => int.TryParse(n.S(key), out var i) ? i : fallback;
    public static double D(this JsonNode? n, string key, double fallback = 0) => double.TryParse(n.S(key), System.Globalization.CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : fallback;
    public static bool B(this JsonNode? n, string key, bool fallback = false) => bool.TryParse(n.S(key), out var b) ? b : fallback;
    public static JsonObject Copy(this JsonObject n) => (JsonObject)n.DeepClone();
    public static IEnumerable<JsonObject> Objects(this JsonNode? n) => (n as JsonArray)?.OfType<JsonObject>() ?? [];
    public static JsonArray Array(IEnumerable<JsonObject> values) => new(values.Select(v => (JsonNode)v.DeepClone()).ToArray());
    public static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N");
    public static string StableId(string prefix, string value) => prefix + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
