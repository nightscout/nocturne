using System.Text.Json;
using Nocturne.Core.Models.Serializers;

namespace Nocturne.Connectors.Core.Utilities;

/// <summary>
///     Shared JSON serialization options for consistent deserialization across all connectors.
///     Using case-insensitive property matching improves resilience against API changes.
/// </summary>
public static class JsonDefaults
{
    /// <summary>
    ///     Default options for deserializing API responses.
    ///     Case-insensitive to handle varying API response formats.
    ///
    ///     The flexible converters let numeric and boolean fields arrive in whatever
    ///     JSON shape a given uploader emits — numbers as strings (e.g. <c>"insulin":"1.5"</c>,
    ///     <c>"carbs":"45"</c>, <c>"duration":"30"</c>) and booleans as strings/ints
    ///     (e.g. <c>"isValid":"true"</c>, <c>"bolusing":1</c>). Without them a single oddly-typed
    ///     value throws a <see cref="JsonException"/> that aborts the whole page fetch —
    ///     observed dropping entire treatment backfills while clean numeric glucose
    ///     entries synced fine. Real-world Nightscout collections (treatments and
    ///     especially the deeply-nested devicestatus pump/loop/openaps payloads) are the
    ///     worst offenders. Registering the converters here applies them to every type
    ///     these options touch (top-level and nested); property-level <c>[JsonConverter]</c>
    ///     attributes on the models still take precedence where present.
    /// </summary>
    public static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new FlexibleDoubleConverter(),
            new FlexibleNullableDoubleConverter(),
            new FlexibleDecimalConverter(),
            new FlexibleNullableDecimalConverter(),
            new FlexibleIntConverter(),
            new FlexibleNullableIntConverter(),
            new FlexibleLongConverter(),
            new FlexibleNullableLongConverter(),
            new FlexibleNonNullableBooleanJsonConverter(),
            new FlexibleBooleanJsonConverter(),
        },
    };
}