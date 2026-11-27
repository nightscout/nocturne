using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models;

/// <summary>
/// Carries the fields AAPS sends on a temp basal (those of <see cref="UploadedAppFields"/> and
/// <see cref="UploadedPumpFields"/>, <c>enteredBy</c> and <c>duration</c>) into the V4 record, so
/// the projected treatment returns them as uploaded.
/// </summary>
/// <remarks>
/// <c>enteredBy</c> is kept apart from <see cref="V4RecordBase.Device"/>, which holds it or else the
/// uploaded <c>device</c>, since the projected temp basal serves its device too and could not tell
/// the two apart. A V4 client's row keeps none and serves the app it names itself by
/// (<see cref="V4ClientApp"/>).
/// </remarks>
public static class TempBasalUploadedFields
{
    public static Dictionary<string, object?>? Keep(Dictionary<string, object?>? record, Treatment treatment)
    {
        record = UploadedPumpFields.Keep(record, treatment);
        record = UploadedAppFields.Keep(record, treatment);
        record = KeptField.Add(record, UploadedAppFields.EnteredByKey, treatment.EnteredBy);
        return KeptField.Add(
            record,
            StateSpanMetadataExtensions.UploadedDurationKey,
            treatment.HasSetDuration ? treatment.Duration : null);
    }

    public static void Restore(Treatment treatment, TempBasal tempBasal)
    {
        UploadedPumpFields.Restore(treatment, tempBasal.AdditionalProperties, tempBasal.PumpRecordId);
        UploadedAppFields.Restore(treatment, tempBasal.AdditionalProperties, tempBasal.App);
        treatment.EnteredBy = KeptField.String(tempBasal.AdditionalProperties, UploadedAppFields.EnteredByKey)
            ?? V4ClientApp(tempBasal);
    }

    /// <summary>The app a V4 client named itself by, on a row it wrote; the decomposer keeps a legacy id on every row it writes.</summary>
    private static string? V4ClientApp(TempBasal tempBasal) =>
        tempBasal.LegacyId is null ? tempBasal.App : null;

    /// <summary>The <c>duration</c> in minutes the uploader sent, when the record kept it.</summary>
    public static double? UploadedDuration(IReadOnlyDictionary<string, object?>? record) =>
        KeptField.Double(record, StateSpanMetadataExtensions.UploadedDurationKey);
}
