using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Nocturne.Core.Models.Serializers;

/// <summary>
/// Reads a <see cref="LoopTargetRange"/> in either form and writes the <c>[min, max]</c> array
/// Loop uploads, which NightscoutKit and LoopFollow decode as <c>[Double]</c>. A range missing
/// either bound is written as <c>null</c>, since a one-number array reads back as a minimum.
/// </summary>
public sealed class LoopTargetRangeArrayConverter : LoopTargetRangeConverter
{
    private static readonly (Type Owner, string Property)[] ServedAsArrays =
    [
        (typeof(LoopOverridePreset), "targetRange"),
        (typeof(LoopProfileSettings), "preMealTargetRange"),
    ];

    /// <summary>
    /// Type info modifier for every serializer whose output is legacy Nightscout JSON (REST, the storage
    /// socket and the upstream write-back): it serves the ranges Loop uploads as arrays. The v4
    /// documents keep the object form by not applying it.
    /// </summary>
    public static void ServeAsArrays(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return;

        foreach (var (owner, name) in ServedAsArrays)
        {
            if (typeInfo.Type != owner)
                continue;

            foreach (var property in typeInfo.Properties.Where(p => p.Name == name))
                property.CustomConverter = new LoopTargetRangeArrayConverter();
        }
    }

    public override void Write(Utf8JsonWriter writer, LoopTargetRange? value, JsonSerializerOptions options)
    {
        if (value is not { MinValue: { } min, MaxValue: { } max })
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        writer.WriteNumberValue(min);
        writer.WriteNumberValue(max);
        writer.WriteEndArray();
    }
}
