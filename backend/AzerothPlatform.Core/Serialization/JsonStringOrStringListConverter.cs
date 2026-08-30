using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzerothPlatform.Core.Serialization;

/// <summary>
/// Accepts either a JSON string or a JSON string array. Progression <c>mpq.json</c> files
/// use <c>"add": "Patch-W.MPQ"</c> (string); the platform also writes arrays.
/// </summary>
public sealed class JsonStringOrStringListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return string.IsNullOrWhiteSpace(value) ? [] : [value.Trim()];
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<string>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    break;
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    var value = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        list.Add(value.Trim());
                    }

                    continue;
                }

                reader.Skip();
            }

            return list;
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            return [];
        }

        throw new JsonException($"Expected a string or string array, got {reader.TokenType}.");
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }
}
