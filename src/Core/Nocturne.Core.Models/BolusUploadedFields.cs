using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models;

/// <summary>
/// Carries the fields AAPS sends on a bolus and reads back (<c>isReadOnly</c>, <c>isBasalInsulin</c>
/// and those of <see cref="UploadedPumpFields"/>) into the V4 bolus, so the projected treatment
/// returns them as uploaded. <c>app</c> is the bolus's own <see cref="V4RecordBase.App"/>.
/// </summary>
public static class BolusUploadedFields
{
    private const string App = "app";
    private const string IsReadOnly = "isReadOnly";
    private const string IsBasalInsulin = "isBasalInsulin";

    public static Dictionary<string, object?>? Keep(Dictionary<string, object?>? record, Treatment treatment)
    {
        record = UploadedPumpFields.Keep(record, treatment);
        record = KeptField.Add(record, IsReadOnly, treatment.IsReadOnly);
        return KeptField.Add(record, IsBasalInsulin, treatment.IsBasalInsulin);
    }

    public static void Restore(Treatment treatment, Bolus bolus)
    {
        UploadedPumpFields.Restore(treatment, bolus.AdditionalProperties, bolus.PumpRecordId);
        treatment.IsReadOnly = KeptField.Bool(bolus.AdditionalProperties, IsReadOnly);
        treatment.IsBasalInsulin = KeptField.Bool(bolus.AdditionalProperties, IsBasalInsulin);
        if (bolus.App is { } app)
            (treatment.AdditionalProperties ??= new())[App] = app;
    }
}
