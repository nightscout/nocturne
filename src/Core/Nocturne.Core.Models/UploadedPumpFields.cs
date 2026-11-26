namespace Nocturne.Core.Models;

/// <summary>
/// Carries the fields AAPS sends on a pump treatment (<c>type</c>, <c>isValid</c>, <c>pumpType</c>,
/// <c>pumpSerial</c>) into a V4 record, so the projected treatment returns them as uploaded.
/// </summary>
/// <remarks>
/// Only <c>isValid: true</c> is kept: a treatment uploaded with <c>isValid: false</c> is stored
/// deleted. <c>pumpId</c> lives in the record's pump record id, which a connector also sets, so it is
/// served only for a record whose uploader described its pump.
/// </remarks>
public static class UploadedPumpFields
{
    private const string Type = "type";
    private const string IsValid = "isValid";
    private const string PumpType = "pumpType";
    private const string PumpSerial = "pumpSerial";

    public static Dictionary<string, object?>? Keep(Dictionary<string, object?>? record, Treatment treatment)
    {
        record = KeptField.Add(record, Type, treatment.BolusType);
        record = KeptField.Add(record, IsValid, treatment.IsValid is true ? true : null);
        record = KeptField.Add(record, PumpType, treatment.PumpType);
        return KeptField.Add(record, PumpSerial, treatment.PumpSerial);
    }

    public static void Restore(Treatment treatment, IReadOnlyDictionary<string, object?>? record, string? pumpRecordId)
    {
        treatment.BolusType = KeptField.String(record, Type);
        treatment.IsValid = KeptField.Bool(record, IsValid) is true ? true : null;
        treatment.PumpType = KeptField.String(record, PumpType);
        treatment.PumpSerial = KeptField.String(record, PumpSerial);
        treatment.PumpId = (treatment.PumpType is not null || treatment.PumpSerial is not null)
            && long.TryParse(pumpRecordId, out var pumpId)
                ? pumpId
                : null;
    }
}
