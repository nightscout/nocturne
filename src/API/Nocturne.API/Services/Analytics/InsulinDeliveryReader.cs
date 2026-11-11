using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Analytics;

public readonly record struct InsulinRecords(
    List<Bolus> ManualBoluses,
    List<Bolus> AlgorithmBoluses,
    List<TempBasal> TempBasals,
    List<BasalInjection> BasalInjections);

/// <summary>
/// The manual boluses and carb intakes of one window, started once so the readers that need both
/// (<see cref="RangeAnalyticsReader"/> and <see cref="InsulinDeliveryReader"/>) share one read.
/// </summary>
public sealed record TreatmentRangeReads(
    Task<IEnumerable<Bolus>> ManualBoluses,
    Task<IEnumerable<CarbIntake>> Carbs)
{
    public static TreatmentRangeReads Start(
        IBolusRepository bolusRepository,
        ICarbIntakeRepository carbIntakeRepository,
        DateTime from,
        DateTime to,
        CancellationToken ct) =>
        new(
            bolusRepository.GetAsync(from, to, null, null, int.MaxValue, descending: false, kind: BolusKind.Manual, ct: ct),
            carbIntakeRepository.GetAsync(from, to, null, null, int.MaxValue, descending: false, ct: ct));
}

/// <summary>
/// <see cref="IStatisticsService.CalculateInsulinDeliveryStatistics"/> and its inputs, with the
/// manual boluses behind <see cref="InsulinDeliveryStatistics.ManualBolus"/>.
/// </summary>
public sealed record InsulinDeliveryRead(InsulinDeliveryStatistics Statistics, List<Bolus> ManualBoluses);

/// <summary>
/// Reads the insulin records of a window (boluses, temp basals, basal injections) and the profile
/// basal schedule that stands in where a temp basal has no scheduled rate, and assembles
/// <see cref="InsulinDeliveryStatistics"/> from them.
/// </summary>
public sealed class InsulinDeliveryReader(
    IBolusRepository bolusRepository,
    ICarbIntakeRepository carbIntakeRepository,
    ITempBasalRepository tempBasalRepository,
    IBasalInjectionRepository basalInjectionRepository,
    IStateSpanRepository stateSpanRepository,
    IBasalSegmentService basalSegments,
    ITherapySettingsResolver therapySettingsResolver,
    IStatisticsService statisticsService,
    TimeProvider timeProvider)
{
    /// <remarks>
    /// Every collection is read uncapped. An AID pump writes a TempBasal and often an SMB every
    /// ~5 minutes, so any fixed limit truncates a multi-month window to its oldest records and
    /// understates every total computed from it.
    /// <paramref name="alongside"/> holds reads the caller started before calling, joined into the
    /// same <see cref="Task.WhenAll(Task[])"/> so a failure here still observes them.
    /// </remarks>
    public Task<InsulinRecords> ReadRecordsAsync(
        DateTime from, DateTime to, CancellationToken ct, params Task[] alongside) =>
        ReadRecordsCoreAsync(
            from,
            to,
            bolusRepository.GetAsync(from, to, null, null, int.MaxValue, descending: false, kind: BolusKind.Manual, ct: ct),
            ct,
            alongside);

    /// <remarks>
    /// Reads from <see cref="StatisticsService.TempBasalReadStart"/>, not the window start: the
    /// repository windows on a temp's start, and one begun before the window can still be running in
    /// it. <see cref="StatisticsService.DeliveredWithin"/> cuts each to the window.
    /// </remarks>
    public Task<IEnumerable<TempBasal>> ReadTempBasalsAsync(DateTime from, DateTime to, CancellationToken ct) =>
        tempBasalRepository.GetAsync(
            StatisticsService.TempBasalReadStart(from), to, null, null, int.MaxValue, descending: false, ct: ct);

    /// <summary>
    /// Reads the profile's basal schedule over the window, none when the tenant has no therapy
    /// settings: <see cref="IBasalSegmentService"/> would otherwise answer with its constant 1.0 U/h
    /// fallback.
    /// </summary>
    public async Task<IReadOnlyList<BasalSegment>> ReadProfileBasalSegmentsAsync(
        DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        if (!await therapySettingsResolver.HasDataAsync(ct)) return [];

        return await basalSegments.GetSegmentsAsync(startUtc.ToMills(), endUtc.ToMills(), ct).ToListAsync(ct);
    }

    /// <summary>
    /// Fills <see cref="TempBasal.ScheduledRate"/> from <see cref="ScheduledBasal"/> at each
    /// temp's start, for records that have none. Legacy-sourced temps arrive with a null rate and
    /// <see cref="IStatisticsService.CalculateBasalAnalysis"/> can only classify a temp as high or
    /// low against it. Scheduled-origin records are the profile baseline itself and are skipped.
    /// The schedule is read from <see cref="StatisticsService.TempBasalReadStart"/>, since the temps
    /// begin that far before the window.
    /// </summary>
    public async Task FillMissingScheduledRatesAsync(
        IList<TempBasal> tempBasals, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var unfilled = tempBasals.Where(tb => !tb.ScheduledRate.HasValue && tb.Origin != TempBasalOrigin.Scheduled).ToList();
        if (unfilled.Count == 0)
            return;

        var readStart = StatisticsService.TempBasalReadStart(startUtc);
        var scheduled = new ScheduledBasal(
            await ReadProfileBasalSegmentsAsync(readStart, endUtc, ct),
            await ScheduledBasal.OverridesAsync(stateSpanRepository, readStart.ToMills(), endUtc.ToMills(), ct));
        foreach (var tb in unfilled)
            tb.ScheduledRate = scheduled.At(tb.StartMills);
    }

    /// <remarks>
    /// <paramref name="treatments"/> holds the window's manual boluses and carbs when the caller has
    /// already started them for another reader; they are read here when <c>null</c>.
    /// </remarks>
    public async Task<InsulinDeliveryRead> ReadAsync(
        DateTime startDate, DateTime endDate, CancellationToken ct, TreatmentRangeReads? treatments = null)
    {
        var startDt = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
        var endDt = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

        treatments ??= TreatmentRangeReads.Start(bolusRepository, carbIntakeRepository, startDt, endDt, ct);

        var (boluses, algorithmBoluses, tempBasals, basalInjections) =
            await ReadRecordsCoreAsync(startDt, endDt, treatments.ManualBoluses, ct, treatments.Carbs);
        var carbs = await treatments.Carbs;
        await FillMissingScheduledRatesAsync(tempBasals, startDt, endDt, ct);

        var result = statisticsService.CalculateInsulinDeliveryStatistics(
            boluses,
            algorithmBoluses,
            tempBasals,
            carbs,
            startDate,
            endDate,
            StatisticsService.UntilMills(endDt, timeProvider.GetUtcNow()),
            basalInjections
        );
        return new InsulinDeliveryRead(result, boluses);
    }

    private async Task<InsulinRecords> ReadRecordsCoreAsync(
        DateTime from, DateTime to, Task<IEnumerable<Bolus>> manualTask, CancellationToken ct, params Task[] alongside)
    {
        var algorithmTask = bolusRepository.GetAsync(from, to, null, null, int.MaxValue, descending: false, kind: BolusKind.Algorithm, ct: ct);
        var tempBasalTask = ReadTempBasalsAsync(from, to, ct);
        var injectionTask = basalInjectionRepository.GetAsync(from, to, null, null, int.MaxValue, 0, false, ct);

        await Task.WhenAll([manualTask, algorithmTask, tempBasalTask, injectionTask, .. alongside]);

        return new InsulinRecords(
            (await manualTask).ToList(),
            (await algorithmTask).ToList(),
            (await tempBasalTask).ToList(),
            (await injectionTask).ToList());
    }
}
