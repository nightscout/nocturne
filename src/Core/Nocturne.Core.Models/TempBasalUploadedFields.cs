using System.Text.Json;

namespace Nocturne.Core.Models;

/// <summary>
/// Carries the fields AAPS sends on a temp basal (<c>type</c>, <c>isValid</c>, <c>pumpType</c>,
/// <c>pumpSerial</c>) into the V4 record, so the projected treatment returns them as uploaded.
/// </summary>
public static class TempBasalUploadedFields
{
    private const string Type = "type";
    private const string IsValid = "isValid";
    private const string PumpType = "pumpType";
    private const string PumpSerial = "pumpSerial";

    public static Dictionary<string, object?>? Keep(Dictionary<string, object?>? record, Treatment treatment)
    {
        if (treatment.BolusType is { } type)
            (record ??= new())[Type] = type;
        if (treatment.IsValid is { } isValid)
            (record ??= new())[IsValid] = isValid;
        if (treatment.PumpType is { } pumpType)
            (record ??= new())[PumpType] = pumpType;
        if (treatment.PumpSerial is { } pumpSerial)
            (record ??= new())[PumpSerial] = pumpSerial;
        if (treatment.HasSetDuration && treatment.Duration is { } duration)
            (record ??= new())[StateSpanMetadataExtensions.UploadedDurationKey] = duration;

        return record;
    }

    public static void Restore(Treatment treatment, IReadOnlyDictionary<string, object?>? record)
    {
        treatment.BolusType = StringOf(record, Type);
        treatment.IsValid = record?.GetValueOrDefault(IsValid) switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => null,
        };
        treatment.PumpType = StringOf(record, PumpType);
        treatment.PumpSerial = StringOf(record, PumpSerial);
    }

    /// <summary>The <c>duration</c> in minutes the uploader sent, when the record kept it.</summary>
    public static double? UploadedDuration(IReadOnlyDictionary<string, object?>? record) =>
        record?.GetValueOrDefault(StateSpanMetadataExtensions.UploadedDurationKey) switch
        {
            double minutes => minutes,
            JsonElement { ValueKind: JsonValueKind.Number } je => je.GetDouble(),
            _ => null,
        };

    private static string? StringOf(IReadOnlyDictionary<string, object?>? record, string key) =>
        record?.GetValueOrDefault(key) switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => null,
        };
}
