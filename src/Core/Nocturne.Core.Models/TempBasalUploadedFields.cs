namespace Nocturne.Core.Models;

/// <summary>
/// Carries the fields AAPS sends on a temp basal (those of <see cref="UploadedPumpFields"/> and
/// <c>duration</c>) into the V4 record, so the projected treatment returns them as uploaded.
/// </summary>
public static class TempBasalUploadedFields
{
    public static Dictionary<string, object?>? Keep(Dictionary<string, object?>? record, Treatment treatment) =>
        KeptField.Add(
            UploadedPumpFields.Keep(record, treatment),
            StateSpanMetadataExtensions.UploadedDurationKey,
            treatment.HasSetDuration ? treatment.Duration : null);

    public static void Restore(Treatment treatment, IReadOnlyDictionary<string, object?>? record, string? pumpRecordId) =>
        UploadedPumpFields.Restore(treatment, record, pumpRecordId);

    /// <summary>The <c>duration</c> in minutes the uploader sent, when the record kept it.</summary>
    public static double? UploadedDuration(IReadOnlyDictionary<string, object?>? record) =>
        KeptField.Double(record, StateSpanMetadataExtensions.UploadedDurationKey);
}
