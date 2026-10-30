using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Mcp.Reports.Datasets;

/// <summary>
/// The insulin and carb records a statistics report reads, read as <c>StatisticsController</c>
/// reads them for its insulin figures: every record uncapped, manual and algorithm boluses apart,
/// temp basals from <see cref="StatisticsService.TempBasalReadStart"/> since one begun earlier can
/// still run in the window.
/// </summary>
internal sealed record InsulinRecords(
    IReadOnlyList<Bolus> ManualBoluses,
    IReadOnlyList<Bolus> AlgorithmBoluses,
    IReadOnlyList<TempBasal> TempBasals,
    IReadOnlyList<BasalInjection> BasalInjections,
    IReadOnlyList<CarbIntake> Carbs)
{
    public int Count => ManualBoluses.Count + AlgorithmBoluses.Count + TempBasals.Count + BasalInjections.Count + Carbs.Count;

    public static async Task<InsulinRecords> ReadAsync(IServiceProvider services, DateTime from, DateTime to, CancellationToken ct)
    {
        var boluses = services.GetRequiredService<IBolusRepository>();
        var manual = await boluses.GetAsync(from, to, null, null, int.MaxValue, descending: false, kind: BolusKind.Manual, ct: ct);
        var algorithm = await boluses.GetAsync(from, to, null, null, int.MaxValue, descending: false, kind: BolusKind.Algorithm, ct: ct);
        var temps = await services.GetRequiredService<ITempBasalRepository>().GetAsync(
            StatisticsService.TempBasalReadStart(from), to, null, null, int.MaxValue, descending: false, ct: ct);
        var injections = await services.GetRequiredService<IBasalInjectionRepository>().GetAsync(from, to, null, null, int.MaxValue, 0, false, ct);
        var carbs = await services.GetRequiredService<ICarbIntakeRepository>().GetAsync(from, to, null, null, int.MaxValue, descending: false, ct: ct);

        return new InsulinRecords([.. manual], [.. algorithm], [.. temps], [.. injections], [.. carbs]);
    }
}
