using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Contexo.Core.Abstractions;

namespace Contexo.Core.Diagnostics;

/// <summary>Turns settings into JSON with every secret-looking value replaced by "***".</summary>
internal static class SettingsMasker
{
    private const string Mask = "***";

    private static readonly string[] SensitiveNames = ["key", "secret", "token", "password"];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string MaskToJson(AppSettings settings) =>
        MaskJson(JsonSerializer.Serialize(settings, Options));

    /// <summary>Masks any property whose name contains key, secret, token or password (any case), recursively through objects and arrays.</summary>
    public static string MaskJson(string json)
    {
        var node = JsonNode.Parse(json);
        MaskNode(node);
        return node?.ToJsonString(Options) ?? "null";
    }

    private static void MaskNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (IsSensitive(name))
                    {
                        obj[name] = Mask;
                    }
                    else
                    {
                        MaskNode(obj[name]);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    MaskNode(item);
                }

                break;
        }
    }

    private static bool IsSensitive(string name) =>
        SensitiveNames.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));
}
