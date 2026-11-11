using Nocturne.API.Services.Caching;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Analytics;

/// <inheritdoc cref="IIobCobReader"/>
/// <remarks>
/// A live reading is held in the local tier of <see cref="ITenantCache"/> for a minute, so a
/// history-clamped reader, which has no cache partition, always computes it. A reading at a past
/// instant (replay, a requested time) is never cached: the key names only the present.
/// </remarks>
public sealed class IobCobReader(
    IIobCalculator iobCalculator,
    ICobCalculator cobCalculator,
    IBolusRepository bolusRepository,
    ITempBasalRepository tempBasalRepository,
    ICarbIntakeRepository carbIntakeRepository,
    ITenantCache cache,
    ILogger<IobCobReader> logger
) : IIobCobReader
{
    private const int Limit = 1000;
    private const string LiveCacheKey = "iobcob:current";
    private static readonly TimeSpan LiveCacheDuration = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    public async Task<IobCobInputs> ReadInputsAsync(long at, CancellationToken ct = default)
    {
        var to = DateTimeOffset.FromUnixTimeMilliseconds(at).UtcDateTime;
        var insulinFrom = to.AddMilliseconds(-DailyProfileCalculator.ActingMs);

        var boluses = await bolusRepository.GetAsync(
            from: insulinFrom, to: to, device: null, source: null,
            limit: Limit, offset: 0, descending: false, ct: ct);
        var tempBasals = await tempBasalRepository.GetAsync(
            from: StatisticsService.TempBasalReadStart(insulinFrom), to: to, device: null, source: null,
            limit: ChartDataService.TempBasalQueryLimit, offset: 0, descending: false, ct: ct);
        var carbIntakes = await carbIntakeRepository.GetAsync(
            from: to.AddHours(-IIobCobReader.CarbLookbackHours), to: to, device: null, source: null,
            limit: Limit, offset: 0, descending: false, ct: ct);

        return new IobCobInputs(boluses.ToList(), tempBasals.ToList(), carbIntakes.ToList());
    }

    /// <inheritdoc />
    public Task<IobCobReading> ReadTotalAsync(long at, bool live, CancellationToken ct = default) =>
        live
            ? cache.GetOrCreateLocalAsync(LiveCacheKey, () => ComputeAsync(at, live: true, ct), LiveCacheDuration, ct)
            : ComputeAsync(at, live: false, ct);

    private async Task<IobCobReading> ComputeAsync(long at, bool live, CancellationToken ct)
    {
        var inputs = await ReadInputsAsync(at, ct);
        var iob = await iobCalculator.CalculateTotalAsync(
            inputs.Boluses, inputs.TempBasals, at, live: live, ct: ct);
        var windowStart = at - DailyProfileCalculator.ActingMs;
        var insulinInWindow = inputs.Boluses.Count > 0
            || inputs.TempBasals.Any(t => t.EndMills is null || t.EndMills >= windowStart);
        return new IobCobReading(iob, await CobAsync(at, live, inputs, ct), inputs)
        {
            InsulinInWindow = insulinInWindow,
        };
    }

    private async Task<CobResult?> CobAsync(long at, bool live, IobCobInputs inputs, CancellationToken ct)
    {
        try
        {
            return await cobCalculator.CalculateTotalAsync(
                inputs.CarbIntakes, inputs.Boluses, inputs.TempBasals, at, live: live, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "COB calculation failed at {At}; reporting IOB without it", at);
            return null;
        }
    }
}
