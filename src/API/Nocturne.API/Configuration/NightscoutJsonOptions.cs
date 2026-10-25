using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Attributes;
using Nocturne.Core.Models.Serializers;

namespace Nocturne.API.Configuration;

/// <summary>
/// JSON serialization options and modifiers for Nightscout-compatible endpoints.
/// <see cref="NightscoutJsonFilter"/> applies them to v1-v3 responses.
/// </summary>
public static class NightscoutJsonOptions
{
    /// <summary>
    /// Creates JsonSerializerOptions configured for Nightscout V1-V3 API compatibility.
    /// </summary>
    public static JsonSerializerOptions Create()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Use WhenWritingDefault to omit null values AND default values (0 for int, false for bool, etc.)
            // This matches Nightscout's behavior of omitting unset fields
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            WriteIndented = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = { IgnoredBasePropertyModifier.RemoveIgnoredBaseProperties, ExcludeNocturneOnlyProperties, HideIdBesideUnderscoreId, OmitIndefiniteDuration, ServeFoodFlagsAsStrings }
            }
        };
    }

    /// <summary>
    /// Modifier that excludes properties marked with [NocturneOnly] attribute.
    /// These properties are specific to Nocturne and should not appear in Nightscout-compatible responses.
    /// </summary>
    public static void ExcludeNocturneOnlyProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return;

        foreach (var property in typeInfo.Properties)
        {
            // Use AttributeProvider to get the actual property/field info with its attributes
            // This works correctly regardless of JsonPropertyName remapping
            if (property.AttributeProvider?.GetCustomAttributes(typeof(NocturneOnlyAttribute), true).Length > 0)
            {
                property.ShouldSerialize = (_, _) => false;
            }
        }
    }

    /// <summary>
    /// Hides a camelCase <c>id</c> on a type that also declares a literal <c>_id</c> property, as the
    /// legacy <see cref="Role"/> and <see cref="Subject"/> do, so the v2 authorization responses keep
    /// Nightscout's single <c>_id</c>.
    /// </summary>
    public static void HideIdBesideUnderscoreId(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || typeInfo.Properties.All(p => p.Name != "_id"))
            return;

        if (typeInfo.Properties.FirstOrDefault(p => p.Name == "id") is { } id)
            id.ShouldSerialize = (_, _) => false;
    }

    /// <summary>
    /// Modifier that serves <see cref="Food.Hidden"/> and <see cref="Food.HideAfterUse"/> as the strings
    /// <c>"true"</c> and <c>"false"</c>, always present. Nightscout's food editor posts form-encoded, so it
    /// stores and echoes strings, and its UI tests <c>hidden === "false"</c>.
    /// </summary>
    public static void ServeFoodFlagsAsStrings(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || typeInfo.Type != typeof(Food))
            return;

        foreach (var property in typeInfo.Properties.Where(p => p.Name is "hidden" or "hideafteruse"))
        {
            property.CustomConverter = new StringBooleanJsonConverter();
            property.ShouldSerialize = (_, _) => true;
        }
    }

    /// <summary>
    /// Modifier that leaves <c>duration</c> out of a treatment with <c>durationType: "indefinite"</c> and
    /// no duration. Loop uploads an open-ended override that way and Nightscout stores it as sent;
    /// NightscoutKit reads any numeric <c>duration</c>, 0 included, as a finite override, and
    /// <see cref="Treatment.Duration"/> otherwise serializes an unset duration as 0.
    /// </summary>
    public static void OmitIndefiniteDuration(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || !typeof(Treatment).IsAssignableFrom(typeInfo.Type))
            return;

        var duration = typeInfo.Properties.FirstOrDefault(p => p.Name == "duration");
        if (duration is null)
            return;

        duration.ShouldSerialize = (treatment, value) =>
            !(((Treatment)treatment).DurationType == "indefinite" && value is null or 0.0);
    }

    private sealed class StringBooleanJsonConverter : FlexibleNonNullableBooleanJsonConverter
    {
        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value ? "true" : "false");
    }
}
