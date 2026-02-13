using Nocturne.Connectors.Core.Constants;
using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Provides consistent treatment classification logic across all connectors.
/// </summary>
public class TreatmentClassificationService : ITreatmentClassificationService
{
    /// <inheritdoc />
    public string ClassifyTreatment(double? carbs, double? insulin)
    {
        var hasCarbs = carbs is > 0;
        var hasInsulin = insulin is > 0;

        return (hasCarbs, hasInsulin) switch
        {
            (true, true) => TreatmentTypes.MealBolus,
            (true, false) => TreatmentTypes.CarbCorrection,
            (false, true) => TreatmentTypes.CorrectionBolus,
            // Edge case: no carbs and no insulin - default to Correction Bolus
            // This shouldn't happen in normal data but provides a fallback
            _ => TreatmentTypes.CorrectionBolus
        };
    }
}