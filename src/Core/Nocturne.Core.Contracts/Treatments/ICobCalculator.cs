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

    CarbCobContribution CalcCarbIntake(CarbIntake carbIntake, long time);
}
