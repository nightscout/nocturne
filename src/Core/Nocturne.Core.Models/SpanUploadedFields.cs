using System.Text.Json;

namespace Nocturne.Core.Models;

/// <summary>
/// Carries the fields AAPS sends on a treatment and reads back (<c>isValid</c>, <c>pumpId</c>,
/// <c>pumpType</c>, <c>pumpSerial</c>) into the metadata of the state span or profile activation it
/// is stored as, so the projected treatment returns them as uploaded.
/// </summary>
public static class SpanUploadedFields
{
    private const string IsValid = "isValid";
    private const string PumpId = "pumpId";
    private const string PumpType = "pumpType";
    private const string PumpSerial = "pumpSerial";

    public static void Keep(Dictionary<string, object> metadata, Treatment treatment)
    {
        if (treatment.IsValid is { } isValid)
            metadata[IsValid] = isValid;
        if (treatment.PumpId is { } pumpId)
            metadata[PumpId] = pumpId;
        if (!string.IsNullOrEmpty(treatment.PumpType))
            metadata[PumpType] = treatment.PumpType;
        if (!string.IsNullOrEmpty(treatment.PumpSerial))
            metadata[PumpSerial] = treatment.PumpSerial;
    }

    public static void Restore(Treatment treatment, IDictionary<string, object>? metadata)
    {
        treatment.IsValid = (metadata is not null && metadata.TryGetValue(IsValid, out var isValid) ? isValid : null) switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => null,
        };
        treatment.PumpId = (long?)metadata.TryReadDecimal(PumpId);
        treatment.PumpType = metadata.TryReadString(PumpType);
        treatment.PumpSerial = metadata.TryReadString(PumpSerial);
    }
}
