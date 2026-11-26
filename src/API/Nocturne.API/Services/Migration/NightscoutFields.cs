using System.Text.Json;
using Nocturne.Connectors.Core.Utilities;

namespace Nocturne.API.Services.Migration;

/// <summary>A property of a Nightscout document held as a <see cref="JsonElement"/>, read by the rule every migrated document is read by.</summary>
internal static class NightscoutFields
{
    /// <returns>
    /// Null when <paramref name="document"/> is not an object, lacks <paramref name="property"/>, or holds a
    /// value <see cref="JsonDefaults.CaseInsensitive"/> cannot read as <typeparamref name="T"/>.
    /// </returns>
    public static T? Of<T>(JsonElement document, string property) =>
        document.ValueKind == JsonValueKind.Object && document.TryGetProperty(property, out var value)
            ? value.Deserialize<T>(JsonDefaults.CaseInsensitive)
            : default;
}
