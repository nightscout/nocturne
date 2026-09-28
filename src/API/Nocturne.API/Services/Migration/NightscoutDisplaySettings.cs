using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Services.Migration;

/// <summary>
/// How a Nightscout instance shows glucose and which timezone its profile runs in, read before its
/// history is imported so onboarding can pre-fill the units and timezone it asks to confirm.
/// </summary>
/// <param name="DisplayUnits">The instance's <c>DISPLAY_UNITS</c>, as "mg/dl" or "mmol".</param>
/// <param name="ProfileUnits">
/// The units the newest profile's default store is written in, as "mg/dl" or "mmol". Its targets
/// and sensitivity are numbers in these units, which can differ from <paramref name="DisplayUnits"/>.
/// </param>
/// <param name="ProfileTimezone">The IANA timezone of the newest profile's default store.</param>
public record NightscoutDisplaySettings(string? DisplayUnits, string? ProfileUnits, string? ProfileTimezone)
{
    /// <summary>
    /// Reads the settings out of <c>api/v1/status.json</c> and <c>api/v1/profile.json</c> bodies.
    /// A value the source does not state, or states in a form not recognised, is null.
    /// </summary>
    /// <exception cref="JsonException">A body is not JSON.</exception>
    public static NightscoutDisplaySettings Parse(string statusJson, string profilesJson)
    {
        using var status = JsonDocument.Parse(statusJson);
        using var profiles = JsonDocument.Parse(profilesJson);

        var displayUnits = status.RootElement.ValueKind == JsonValueKind.Object
            && status.RootElement.TryGetProperty("settings", out var settings)
            && settings.ValueKind == JsonValueKind.Object
                ? GlucoseUnitDefaults.Normalize(StringOf(settings, "units"))
                : null;

        if (NewestProfile(profiles.RootElement) is not { } profile)
            return new NightscoutDisplaySettings(displayUnits, null, null);

        var store = DefaultStore(profile);
        var profileUnits = GlucoseUnitDefaults.Normalize(StringOf(store, "units") ?? StringOf(profile, "units"));
        var timezone = StringOf(store, "timezone")?.Trim();

        return new NightscoutDisplaySettings(
            displayUnits, profileUnits, string.IsNullOrEmpty(timezone) ? null : timezone);
    }

    /// <summary>The profile document with the latest start, the one Nightscout treats as current.</summary>
    private static JsonElement? NewestProfile(JsonElement documents)
    {
        if (documents.ValueKind != JsonValueKind.Array)
            return null;

        JsonElement? newest = null;
        var newestStart = DateTimeOffset.MinValue;
        foreach (var document in documents.EnumerateArray())
        {
            if (document.ValueKind != JsonValueKind.Object)
                continue;

            var start = StartOf(document);
            if (newest is null || start > newestStart)
            {
                newest = document;
                newestStart = start;
            }
        }

        return newest;
    }

    private static DateTimeOffset StartOf(JsonElement document)
    {
        if (UploaderTimestamp.TryParse(StringOf(document, "startDate"), out var start))
            return start;

        return document.TryGetProperty("mills", out var mills) && mills.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.MinValue;
    }

    private static JsonElement? DefaultStore(JsonElement profile)
    {
        if (!profile.TryGetProperty("store", out var store) || store.ValueKind != JsonValueKind.Object)
            return null;

        var name = StringOf(profile, "defaultProfile");
        if (name is not null && store.TryGetProperty(name, out var named) && named.ValueKind == JsonValueKind.Object)
            return named;

        return null;
    }

    private static string? StringOf(JsonElement? element, string property) =>
        element?.TryGetProperty(property, out var value) == true && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
