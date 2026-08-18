using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Serializes armory layout JSON for the Node runtime (<c>static/data/armory-layout.json</c>).
/// Must use camelCase property names - the armory loader reads <c>widgets</c>, not <c>Widgets</c>.
/// </summary>
public static class ArmoryLayoutSerialization
{
    public static JsonSerializerOptions RuntimeJsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ToRuntimeJson(ArmoryLayoutDto layout) =>
        JsonSerializer.Serialize(layout, RuntimeJsonOptions);

    public static ArmoryLayoutDto? FromRuntimeJson(string json) =>
        JsonSerializer.Deserialize<ArmoryLayoutDto>(json, RuntimeJsonOptions);
}
