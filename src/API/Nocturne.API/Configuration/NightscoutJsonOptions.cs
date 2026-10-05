using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Attributes;

namespace Nocturne.API.Configuration;

/// <summary>
/// Shared JSON serialization options and modifiers for Nightscout-compatible endpoints.
/// Used by both NightscoutJsonOutputFormatter and NightscoutJsonFilter.
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
                Modifiers = { ExcludeNocturneOnlyProperties, RemoveDuplicateIdProperty, OmitIndefiniteDuration }
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
    /// Modifier that removes duplicate "id" property when "_id" already exists.
    /// This handles System.Text.Json's behavior with interface implementations where
    /// the interface property might be serialized separately from the class override.
    /// </summary>
    public static void RemoveDuplicateIdProperty(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return;

        // Check if we have both "_id" and "id" properties
        bool hasUnderscoreId = typeInfo.Properties.Any(p => p.Name == "_id");
        if (hasUnderscoreId)
        {
            // Remove "id" property since we already have "_id"
            var idProperty = typeInfo.Properties.FirstOrDefault(p => p.Name == "id");
            if (idProperty != null)
            {
                idProperty.ShouldSerialize = (_, _) => false;
            }
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
}
