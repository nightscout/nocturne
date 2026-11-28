using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models;

/// <summary>
/// Carries the fields AAPS sends on a bolus and reads back (those of <see cref="UploadedAppFields"/>
/// and <see cref="UploadedPumpFields"/>, and <c>isBasalInsulin</c>) into the V4 bolus, so the
/// projected treatment returns them as uploaded.
/// </summary>
public static class BolusUploadedFields
{
    private const string IsBasalInsulin = "isBasalInsulin";

    public static Dictionary<string, object?>? Keep(Dictionary<string, object?>? record, Treatment treatment)
    {
        record = UploadedPumpFields.Keep(record, treatment);
        record = UploadedAppFields.KeepAll(record, treatment);
        return KeptField.Add(record, IsBasalInsulin, treatment.IsBasalInsulin);
    }

    public static void Restore(Treatment treatment, Bolus bolus)
    {
        UploadedPumpFields.Restore(treatment, bolus.AdditionalProperties, bolus.PumpRecordId);
        UploadedAppFields.RestoreAll(treatment, bolus.AdditionalProperties, bolus.App, bolus.Device);
        treatment.IsBasalInsulin = KeptField.Bool(bolus.AdditionalProperties, IsBasalInsulin);
    }
}
