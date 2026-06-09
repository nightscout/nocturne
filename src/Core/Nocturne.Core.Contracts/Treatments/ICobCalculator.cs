using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Treatments;

public interface ICobCalculator
{
    Task<CobResult> CalculateTotalAsync(
        List<CarbIntake> carbIntakes,
        List<Bolus>? boluses = null,
        List<TempBasal>? tempBasals = null,
        long? time = null,
        CancellationToken ct = default);

    CobResult FromCarbIntakes(
        List<CarbIntake> carbIntakes,
        List<Bolus>? boluses = null,
        List<TempBasal>? tempBasals = null,
        long? time = null);

    /// <summary>
    /// Async-free overload for the chart COB tick loop. Resolves carb absorption, sensitivity and
    /// carb ratio from the in-memory <paramref name="snapshot"/>, and derives insulin activity for
    /// the dynamic carb-delay calculation directly from boluses (the only treatments that carry
    /// activity) via the snapshot-based IOB path — so no profile resolver or device-snapshot DB
    /// query is issued per carb per tick. See IobCobComputeStage.
    /// </summary>
    CobResult FromCarbIntakes(
        List<CarbIntake> carbIntakes,
        List<Bolus>? boluses,
        List<TempBasal>? tempBasals,
        TherapySnapshot snapshot,
        long time);

    CarbCobContribution CalcCarbIntake(CarbIntake carbIntake, long time);
}
