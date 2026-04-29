using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Treatments;

public interface IIobCalculator
{
    Task<IobResult> CalculateTotalAsync(
        List<Bolus> boluses,
        List<TempBasal>? tempBasals = null,
        long? time = null,
        CancellationToken ct = default);

    IobResult FromBoluses(List<Bolus> boluses, long? time = null);
    IobResult FromTempBasals(List<TempBasal> tempBasals, long? time = null);
    IobContribution CalcBolus(Bolus bolus, long? time = null);
    IobContribution CalcTempBasal(TempBasal tempBasal, long? time = null);
}
