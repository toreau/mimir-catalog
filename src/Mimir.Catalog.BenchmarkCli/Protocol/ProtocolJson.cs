using System.Text.Json;

namespace Mimir.Catalog.BenchmarkCli.Protocol;

/// <summary>Deterministic System.Text.Json configuration shared by the protocol.</summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    /// <summary>Serializes exactly one JSON document (no trailing content).</summary>
    public static void WriteSingleDocument(TextWriter writer, object value)
    {
        writer.Write(JsonSerializer.Serialize(value, value.GetType(), Options));
        writer.Write('\n');
    }

    /// <summary>
    /// Strictly deserializes one JSON object from UTF-8 bytes. JsonDocument
    /// rejects trailing non-whitespace content after the single root value.
    /// </summary>
    public static T DeserializeStrict<T>(ReadOnlyMemory<byte> bytes)
        where T : class
    {
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });
        return JsonSerializer.Deserialize<T>(doc.RootElement, Options)
               ?? throw new JsonException("payload produced null");
    }

    public static string ToJson(object value) => JsonSerializer.Serialize(value, value.GetType(), Options);
}
