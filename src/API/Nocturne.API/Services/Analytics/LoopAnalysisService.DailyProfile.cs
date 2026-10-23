using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Schedules;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Analytics;

internal sealed partial class LoopAnalysisService
{
    public async Task<DailyProfileResponse> GetDailyProfileAsync(
        LoopAnalysisFilter filter, TimeZoneInfo zone, CancellationToken ct = default)
    {
        var (primary, comparison, names) = await ForRangesAsync(
            filter,
            window => LoadDailyProfileAsync(window, zone, filter, BasalIobModel.Displayed, ct),
            (loaded, window) => DailyProfileCalculator.Calculate(
                loaded.Inputs, Mask(filter, window, zone, loaded.Sources), window.From, window.To));
        return new DailyProfileResponse { Primary = primary, Comparison = comparison, ProfileNames = names };
    }

    /// <param name="Timeline">The therapy settings in force over the window, which the IOB/COB was computed against.</param>
    private sealed record DailyProfileLoaded(
        DailyProfileInputs Inputs, LoopAnalysisMaskSources Sources, TherapyTimeline Timeline) : ILoaded;

    /// <param name="filter">Gives now, which a temp basal still running stops at, and the history clamp's visible edge.</param>
    /// <param name="basalModel">How the calculated IOB counts temp basals.</param>
    private async Task<DailyProfileLoaded> LoadDailyProfileAsync(
        Window window, TimeZoneInfo zone, LoopAnalysisFilter filter, BasalIobModel basalModel, CancellationToken ct)
    {
        var from = window.From;
        var to = window.To;
        var lookbackUtc = ToUtc(from - DailyProfileCalculator.ActingMs);

        var carbTask = carbIntakeRepository.GetAsync(lookbackUtc, ToUtc(to), null, null, int.MaxValue, descending: false, ct: ct);
        var apsTask = apsSnapshotRepository.GetIobCobPointsAsync(lookbackUtc, ToUtc(to), ct);
        var commonTask = LoadCommonAsync(window, ct);
        await Task.WhenAll(carbTask, apsTask, commonTask);

        var carbs = (await carbTask).ToList();
        var apsPoints = await apsTask;
        var common = await commonTask;
        var now = ToMills(filter.Now);
        var delivery = await DeliveryAsync(common, window, now, ct);

        var iobCob = IobCobSeries.Build(
            iobCalculator, cobCalculator, common.Boluses, carbs, common.TempBasals, delivery.Timeline, apsPoints,
            delivery.TickStart, to - 1, Math.Min(to - 1, now), TickMs, basalModel, ct);

        var inputs = new DailyProfileInputs
        {
            Zone = zone,
            Glucose = [.. common.Glucose.Select(g => new GlucoseSample(g.Mills, g.Mgdl))],
            Insulin = delivery.Insulin,
            Carbs = [.. carbs.Where(c => c.Mills >= from && c.Mills < to).Select(c => new CarbEvent(c.Mills, c.Carbs))],
            IobCob = iobCob,
            IobCobIntervalMs = TickMs,
            HistoryVisibleFrom = filter.HistoryVisibleFrom is { } edge ? ToMills(edge) : null,
            CalculatedIobCobTime = DailyProfileCalculator.CalculableIobCobTime(
                delivery.Evidence,
                common.Boluses.Where(b => b.Insulin > 0).Select(b => b.Mills)
                    .Concat(carbs.Where(c => c.Carbs > 0).Select(c => c.Mills))),
            TargetAt = await TargetRangesAsync(
                delivery.HasTherapy ? common.Profiles : [], ActiveTemporaryTargets(common.Spans, to), zone, to, ct),
        };

        return new DailyProfileLoaded(inputs, Sources(common, SnapshotMills(apsPoints.Select(p => p.Timestamp)), to), delivery.Timeline);
    }

    /// <summary>
    /// The target range in force at an instant: a running temporary target, read in mg/dL as
    /// <see cref="TemporaryTargetUnits"/> reads it, else the schedule. The schedule is the
    /// activation's own targets when it carries them, else the profile's stored version in force, as
    /// <see cref="ActiveSchedule"/> reads it for one instant, from versions read once per profile.
    /// </summary>
    private async Task<Func<long, (double Low, double High)?>> TargetRangesAsync(
        IReadOnlyList<ProfileSpan> profiles,
        IEnumerable<(StateSpan Span, MillsInterval Interval)> temporaryTargets,
        TimeZoneInfo zone,
        long to,
        CancellationToken ct)
    {
        var versions = await VersionsByProfileAsync(profiles, targetRangeScheduleRepository, ct);

        var temporary = StepTimeline<(double Low, double High)>.LatestStartedWins(temporaryTargets
            .Select(t => (t.Interval, Range: TemporaryTargetRange(t.Span)))
            .Where(t => t.Range is not null)
            .Select(t => new Step<(double Low, double High)>(t.Interval.Start, t.Interval.End, t.Range!.Value)));
        var scheduled = StepTimeline<ProfileSpan>.LatestStartedWins(
            profiles.Select(p => new Step<ProfileSpan>(p.StartMills, p.EndMills ?? to, p)));

        return mills =>
        {
            if (temporary.TryAt(mills, out var target))
                return target;
            if (!scheduled.TryAt(mills, out var span))
                return null;
            var entries = span.Schedules?.Targets
                ?? ProfileVersionCache.ActiveAt(
                    versions[span.ProfileName], DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime)?.Entries;
            if (entries is null)
                return null;
            var shifted = mills + (span.Adjustment?.TimeshiftMs ?? 0);
            return ScheduleResolution.FindRangeAtTime(entries, ScheduleTimeHelper.GetSecondsFromMidnight(shifted, zone));
        };
    }

    private static (double Low, double High)? TemporaryTargetRange(StateSpan span)
    {
        var (top, bottom) = TemporaryTargetUnits.ToMgdl(
            (double?)span.Metadata.TryReadDecimal("targetTop"),
            (double?)span.Metadata.TryReadDecimal("targetBottom"),
            span.Metadata.TryReadString("units"));
        return (bottom ?? top, top ?? bottom) is (double low, double high) ? (low, high) : null;
    }
}
