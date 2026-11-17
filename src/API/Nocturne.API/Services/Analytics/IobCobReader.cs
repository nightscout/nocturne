using Nocturne.API.Services.Caching;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Analytics;

/// <inheritdoc cref="IIobCobReader"/>
/// <remarks>
/// A live reading is held in the local tier of <see cref="ITenantCache"/> for a minute, so a
/// history-clamped reader, which has no cache partition, always computes it. A reading at a past
/// instant (replay, a requested time) is never cached: the key names only the present. A series is
/// never cached.
/// </remarks>
public sealed class IobCobReader(
    IIobCalculator iobCalculator,
    ICobCalculator cobCalculator,
    IBolusRepository bolusRepository,
    ITempBasalRepository tempBasalRepository,
    ICarbIntakeRepository carbIntakeRepository,
    ITherapyTimelineResolver therapyTimelineResolver,
    IStateSpanRepository stateSpanRepository,
    IApsSnapshotRepository apsSnapshotRepository,
    IPumpSnapshotRepository pumpSnapshotRepository,
    ITenantCache cache,
    ILogger<IobCobReader> logger
) : IIobCobReader, IIobCobSeriesReader
{
    private const int Limit = 1000;

    /// <summary>
    /// The most boluses, and the most carb intakes, a series reads; temp basals are read under
    /// <see cref="ChartDataService.TempBasalQueryLimit"/>. The read is ascending, so a window that
    /// holds more loses its newest records.
    /// </summary>
    private const int SeriesTreatmentLimit = 5000;

    private const string LiveCacheKey = "iobcob:current";
    private static readonly TimeSpan LiveCacheDuration = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    public async Task<IobCobInputs> ReadInputsAsync(long at, CancellationToken ct = default)
    {
        var to = at.ToUtcDateTime();
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

    /// <inheritdoc />
    public async Task<IobCobSeriesReading> ReadSeriesAsync(
        IobCobSeriesWindow window, long nowMills, bool readCarbs, CancellationToken ct = default)
    {
        var to = window.ReadEnd.ToUtcDateTime();
        var insulinFrom = window.InsulinReadStart.ToUtcDateTime();

        List<Bolus> boluses = [.. await bolusRepository.GetAsync(
            from: insulinFrom, to: to, device: null, source: null,
            limit: SeriesTreatmentLimit, offset: 0, descending: false, ct: ct)];
        List<TempBasal> tempBasals = [.. await tempBasalRepository.GetAsync(
            from: window.TempBasalReadStart.ToUtcDateTime(), to: to,
            device: null, source: null,
            limit: ChartDataService.TempBasalQueryLimit, offset: 0, descending: false, ct: ct)];
        List<CarbIntake> carbIntakes = readCarbs
            ? [.. await carbIntakeRepository.GetAsync(
                from: insulinFrom, to: to, device: null, source: null,
                limit: SeriesTreatmentLimit, offset: 0, descending: false, ct: ct)]
            : [];

        var timeline = await therapyTimelineResolver.BuildAsync(window.TherapyStart, window.TimelineEnd, ct: ct);
        var scheduled = new ScheduledBasal(
            timeline.BasalSegments(),
            await ScheduledBasal.OverridesAsync(stateSpanRepository, window.TherapyStart, window.OverrideEnd, ct));

        var deviceFrom = window.DeviceReadStart.ToUtcDateTime();
        var deviceTo = window.DeviceReadEnd.ToUtcDateTime();
        var devices = DeviceReadings.Of(
            await apsSnapshotRepository.GetIobCobPointsAsync(deviceFrom, deviceTo, ct),
            await pumpSnapshotRepository.GetIobPointsAsync(deviceFrom, deviceTo, ct));

        var ticks = IobCobSeries.Build(
            iobCalculator, cobCalculator, boluses, carbIntakes, tempBasals, timeline, scheduled, devices,
            window.FirstTick, window.LastTick, nowMills, window.IntervalMs, ct);

        return new IobCobSeriesReading(ticks, boluses, carbIntakes, tempBasals, timeline, scheduled);
    }
}
