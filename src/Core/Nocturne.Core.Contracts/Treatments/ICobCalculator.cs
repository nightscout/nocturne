using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Treatments;

public interface ICobCalculator
{
    /// <param name="live">As for <see cref="IIobCalculator.CalculateTotalAsync"/>.</param>
    Task<CobResult> CalculateTotalAsync(
        List<CarbIntake> carbIntakes,
        List<Bolus>? boluses,
        List<TempBasal>? tempBasals,
        long time,
        bool live = false,
        CancellationToken ct = default);

    /// <summary>
    /// Async-free overload for the chart COB tick loop. Resolves carb absorption, sensitivity and
    /// carb ratio from the in-memory <paramref name="snapshot"/>, and derives insulin activity for
    /// the dynamic carb-delay calculation from the boluses and net basal via the snapshot-based IOB
    /// path, so no profile resolver or device-snapshot DB query is issued per carb per tick. See
    /// IobCobSeries.
    /// </summary>
    /// <param name="tempBasals">Pieces, as for <see cref="IIobCalculator.FromTempBasals"/>.</param>
    /// <param name="scheduledUnder">As for <see cref="IIobCalculator.FromTempBasals"/>.</param>
    CobResult FromCarbIntakes(
        List<CarbIntake> carbIntakes,
        List<Bolus>? boluses,
        List<TempBasal>? tempBasals,
        TherapySnapshot snapshot,
        Func<TempBasal, long, double> scheduledUnder,
        long time);
}
