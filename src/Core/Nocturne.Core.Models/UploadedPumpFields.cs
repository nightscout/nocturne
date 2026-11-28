namespace Nocturne.Core.Models;

/// <summary>
/// Carries the fields AAPS sends on a pump treatment (<c>type</c>, <c>isValid</c>, <c>pumpType</c>,
/// <c>pumpSerial</c>) into a V4 record, or into the metadata of a span (<see cref="KeepInMetadata"/>), so the
/// projected treatment returns them as uploaded.
/// </summary>
/// <remarks>
/// Only <c>isValid: true</c> is kept: a treatment uploaded with <c>isValid: false</c> is stored
/// deleted. On a record, <c>pumpId</c> lives in the pump record id, which a connector also sets, so it is
/// served only for a record whose uploader described its pump.
/// </remarks>
public static class UploadedPumpFields
{
    private const string Type = "type";
    private const string IsValid = "isValid";
    private const string PumpType = "pumpType";
    private const string PumpSerial = "pumpSerial";

    private const string PumpId = "pumpId";

    public static Dictionary<string, object?>? Keep(Dictionary<string, object?>? record, Treatment treatment) =>
        KeepIdentity(KeptField.Add(record, Type, treatment.BolusType), treatment, keepEmpty: true);

    public static void Restore(Treatment treatment, IReadOnlyDictionary<string, object?>? record, string? pumpRecordId)
    {
        treatment.BolusType = KeptField.String(record, Type);
        RestoreIdentity(treatment, record);
        treatment.PumpId = (treatment.PumpType is not null || treatment.PumpSerial is not null)
            && long.TryParse(pumpRecordId, out var pumpId)
                ? pumpId
                : null;
    }

    /// <summary>
    /// Keeps <c>isValid</c>, <c>pumpType</c>, <c>pumpSerial</c> and the <c>pumpId</c> in the metadata of the
    /// state span or profile activation the treatment is stored as, with no <c>type</c>. An empty
    /// <c>pumpType</c> or <c>pumpSerial</c> is not kept there, unlike a record, which keeps it as <c>""</c>.
    /// </summary>
    public static void KeepInMetadata(IDictionary<string, object> metadata, Treatment treatment) =>
        KeptField.CopyInto(
            metadata,
            KeptField.Add(KeepIdentity(null, treatment, keepEmpty: false), PumpId, treatment.PumpId));

    /// <summary>Serves what <see cref="KeepInMetadata"/> kept; the <c>pumpId</c> is served whatever else was kept.</summary>
    public static void RestoreFromMetadata(Treatment treatment, IDictionary<string, object>? metadata)
    {
        RestoreIdentity(treatment, KeptField.OfMetadata(metadata));
        treatment.PumpId = (long?)metadata.TryReadDecimal(PumpId);
    }

    private static Dictionary<string, object?>? KeepIdentity(
        Dictionary<string, object?>? record, Treatment treatment, bool keepEmpty)
    {
        record = KeptField.Add(record, IsValid, treatment.IsValid is true ? true : null);
        record = KeptField.Add(record, PumpType, Kept(treatment.PumpType, keepEmpty));
        return KeptField.Add(record, PumpSerial, Kept(treatment.PumpSerial, keepEmpty));
    }

    private static string? Kept(string? value, bool keepEmpty) => keepEmpty || value is not "" ? value : null;

    private static void RestoreIdentity(Treatment treatment, IReadOnlyDictionary<string, object?>? record)
    {
        treatment.IsValid = KeptField.Bool(record, IsValid) is true ? true : null;
        treatment.PumpType = KeptField.String(record, PumpType);
        treatment.PumpSerial = KeptField.String(record, PumpSerial);
    }
}
