using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Contexo.Core.Abstractions;

namespace Contexo.Core.Storage;

/// <summary>JSON used inside database columns (location, spreadsheet table, excluded subfolders). Chinese stays readable.</summary>
internal static class StoreJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string SerializeLocation(SourceLocation location) => JsonSerializer.Serialize(location, Options);

    public static SourceLocation DeserializeLocation(string json) =>
        JsonSerializer.Deserialize<SourceLocation>(json, Options) ?? SourceLocation.None;

    public static string SerializeTable(SpreadsheetTable table) => JsonSerializer.Serialize(table, Options);

    public static SpreadsheetTable DeserializeTable(string json) =>
        JsonSerializer.Deserialize<SpreadsheetTable>(json, Options)
        ?? throw new InvalidDataException("excel_tables.data is empty.");

    public static string SerializeStrings(IReadOnlyList<string> values) => JsonSerializer.Serialize(values, Options);

    public static IReadOnlyList<string> DeserializeStrings(string json) =>
        JsonSerializer.Deserialize<List<string>>(json, Options) ?? [];
}
