namespace Nocturne.Core.Models;

/// <summary>
/// Carries the fields AAPS sends on a temp basal (<c>type</c>, <c>isValid</c>, <c>pumpType</c>,
/// <c>pumpSerial</c>) into the V4 record, so the projected treatment returns them as uploaded.
/// </summary>
/// <remarks>
/// Only <c>isValid: true</c> is kept: a treatment uploaded with <c>isValid: false</c> is stored
/// deleted.
/// </remarks>
public static class TempBasalUploadedFields
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
        record = KeptField.Add(record, PumpSerial, treatment.PumpSerial);
        return KeptField.Add(
            record,
            StateSpanMetadataExtensions.UploadedDurationKey,
            treatment.HasSetDuration ? treatment.Duration : null);
    }

    public static void Restore(Treatment treatment, IReadOnlyDictionary<string, object?>? record)
    {
        treatment.BolusType = KeptField.String(record, Type);
        treatment.IsValid = KeptField.Bool(record, IsValid) is true ? true : null;
        treatment.PumpType = KeptField.String(record, PumpType);
        treatment.PumpSerial = KeptField.String(record, PumpSerial);
    }

    /// <summary>The <c>duration</c> in minutes the uploader sent, when the record kept it.</summary>
    public static double? UploadedDuration(IReadOnlyDictionary<string, object?>? record) =>
        KeptField.Double(record, StateSpanMetadataExtensions.UploadedDurationKey);
}
