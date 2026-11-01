using System.Text.Json;

namespace Nocturne.API.Services.Migration;

/// <summary>
/// The glucose thresholds and alarm switches a Nightscout instance runs with, read before its
/// history is imported so the setup hub can start its alerts from the numbers the person chose.
/// </summary>
/// <param name="BgLow">The urgent low threshold, in mg/dL whatever the instance's display units.</param>
/// <param name="BgTargetBottom">The low threshold, in mg/dL.</param>
/// <param name="BgTargetTop">The high threshold, in mg/dL.</param>
/// <param name="BgHigh">The urgent high threshold, in mg/dL.</param>
public record NightscoutAlarmSettings(
    decimal? BgLow,
    decimal? BgTargetBottom,
    decimal? BgTargetTop,
    decimal? BgHigh,
    bool? AlarmUrgentLow,
    bool? AlarmLow,
    bool? AlarmHigh,
    bool? AlarmUrgentHigh,
    bool? AlarmTimeagoWarn)
{
    /// <summary>
    /// Reads the settings out of an <c>api/v1/status.json</c> body. A value the source does not
    /// state, or states in a form not recognised, is null.
    /// </summary>
    /// <exception cref="JsonException">The body is not JSON.</exception>
    public static NightscoutAlarmSettings Parse(string statusJson)
    {
        using var status = JsonDocument.Parse(statusJson);

        if (status.RootElement.ValueKind != JsonValueKind.Object
            || !status.RootElement.TryGetProperty("settings", out var settings)
            || settings.ValueKind != JsonValueKind.Object)
            return new NightscoutAlarmSettings(null, null, null, null, null, null, null, null, null);

        var thresholds = settings.TryGetProperty("thresholds", out var found) ? found : default;
        return new NightscoutAlarmSettings(
            NumberOf(thresholds, "bgLow"),
            NumberOf(thresholds, "bgTargetBottom"),
            NumberOf(thresholds, "bgTargetTop"),
            NumberOf(thresholds, "bgHigh"),
            FlagOf(settings, "alarmUrgentLow"),
            FlagOf(settings, "alarmLow"),
            FlagOf(settings, "alarmHigh"),
            FlagOf(settings, "alarmUrgentHigh"),
            FlagOf(settings, "alarmTimeagoWarn"));
    }

    /// <summary>The one shape <see cref="Nocturne.Infrastructure.Data.Entities.MigrationRunEntity.SourceAlarmSettings"/> is written and read in.</summary>
    public string ToJson() => JsonSerializer.Serialize(this);

    /// <returns>Null when <paramref name="json"/> is not a stored <see cref="NightscoutAlarmSettings"/>.</returns>
    public static NightscoutAlarmSettings? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<NightscoutAlarmSettings>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static decimal? NumberOf(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDecimal(out var number)
            ? number
            : null;

    private static bool? FlagOf(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
