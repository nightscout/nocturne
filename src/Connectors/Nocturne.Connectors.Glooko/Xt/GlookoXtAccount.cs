using System.Text.Json;

namespace Nocturne.Connectors.Glooko.Xt;

/// <summary>
///     The few account settings the connector reads from <c>GET_USER_DATA</c>. The answer is the
///     patient's whole profile — name, birth date, national health number — and none of that is
///     kept: only the glucose unit every bare <c>glycemia</c> number is written in, the zone, and
///     the target band. Parsed by hand from the JSON so nothing else is ever materialised.
/// </summary>
public sealed class GlookoXtAccount
{
    /// <summary><c>mg/dl</c> or <c>mmol/l</c>, as the profile spells it.</summary>
    public string? BloodGlucoseUnit { get; init; }

    public string? TimeZoneId { get; init; }
    public double? TargetFrom { get; init; }
    public double? TargetTo { get; init; }

    /// <summary>The unit as the mapper wants it, or null when the profile did not say.</summary>
    public GlookoXtGlucoseUnitSetting? Unit => BloodGlucoseUnit?.Trim().ToLowerInvariant() switch
    {
        "mg/dl" or "mgdl" or "mg" => GlookoXtGlucoseUnitSetting.MgDl,
        "mmol/l" or "mmol" => GlookoXtGlucoseUnitSetting.Mmol,
        _ => null,
    };

    public static GlookoXtAccount? TryParse(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (string.IsNullOrWhiteSpace(text)) return null;
            using var nested = JsonDocument.Parse(text);
            return TryParse(nested.RootElement.Clone());
        }

        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object) return null;

        string? Str(string name) => profile.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? Num(string name) => profile.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        return new GlookoXtAccount
        {
            BloodGlucoseUnit = Str("blood_glucose_unit"),
            TimeZoneId = Str("timezone"),
            TargetFrom = Num("target_from"),
            TargetTo = Num("target_to"),
        };
    }
}

public enum GlookoXtGlucoseUnitSetting
{
    MgDl,
    Mmol,
}
