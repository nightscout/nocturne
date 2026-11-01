using Nocturne.API.Services.ChartData;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Clinical.Devices;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Analytics;

/// <summary>
/// Fetches one window of everything Loop Analysis reads and hands it to the Core calculators.
/// Each page's load and calculation is in its own part (<c>LoopAnalysisService.&lt;Page&gt;.cs</c>);
/// this one holds what the pages share. Every collection is read uncapped, for the reason given on
/// <see cref="InsulinDeliveryReader.ReadRecordsAsync"/>.
/// </summary>
internal sealed partial class LoopAnalysisService(
    ISensorGlucoseRepository glucoseRepository,
    ICanonicalGlucoseService canonicalGlucose,
    IBolusRepository bolusRepository,
    ICarbIntakeRepository carbIntakeRepository,
    ITempBasalRepository tempBasalRepository,
    IApsSnapshotRepository apsSnapshotRepository,
    IPumpSnapshotRepository pumpSnapshotRepository,
    IStateSpanRepository stateSpanRepository,
    IPatientDeviceRepository patientDeviceRepository,
    ITargetRangeScheduleRepository targetRangeScheduleRepository,
    ITherapyTimelineResolver therapyTimelineResolver,
    ITherapySettingsResolver therapySettingsResolver,
    IActiveProfileResolver activeProfileResolver,
    IBasalSeriesBuilder basalSeriesBuilder,
    IBasalSegmentService basalSegmentService,
    IIobCalculator iobCalculator,
    ICobCalculator cobCalculator,
    ITherapySettingsRepository therapySettingsRepository,
    AidSystemMetricsReader aidSystemMetrics,
    DeviceDateClockResolver deviceDates,
    IDeviceEventRepository deviceEventRepository
) : ILoopAnalysisService
{
    private const long TickMs = DailyProfileCalculator.SlotMinutes * LocalSlotBinning.MinuteMs;

    private static readonly StateSpanCategory[] MaskCategories =
    [
        StateSpanCategory.Override,
        StateSpanCategory.TemporaryTarget,
        StateSpanCategory.Exercise,
        StateSpanCategory.Illness,
        StateSpanCategory.DataExclusion,
        StateSpanCategory.PumpMode,
    ];

    /// <summary>What every page loads for a window: at least the sources of its masks.</summary>
    private interface ILoaded
    {
        LoopAnalysisMaskSources Sources { get; }
    }

    private sealed record Ranges<TRange>(TRange Primary, TRange? Comparison, List<string> ProfileNames);

    /// <summary>
    /// Computes the primary range and the comparison: from one load over both where they touch or
    /// overlap, else one load each. The profile names are those of the loaded
    /// <see cref="LoopAnalysisMaskSources.Profiles"/> active in either range, oldest first.
    /// </summary>
    private static Task<Ranges<TRange>> ForRangesAsync<TLoaded, TRange>(
        LoopAnalysisFilter filter, Func<Window, Task<TLoaded>> load, Func<TLoaded, Window, TRange> calculate)
        where TLoaded : ILoaded
        where TRange : class =>
        ForRangesAsync(filter, load, (loaded, window, _) => calculate(loaded, window));

    /// <summary>
    /// As the overload above, with <paramref name="calculate"/> also told whether it is computing the
    /// primary range.
    /// </summary>
    private static async Task<Ranges<TRange>> ForRangesAsync<TLoaded, TRange>(
        LoopAnalysisFilter filter, Func<Window, Task<TLoaded>> load, Func<TLoaded, Window, bool, TRange> calculate)
        where TLoaded : ILoaded
        where TRange : class
    {
        var primary = Window.Of(filter.From, filter.To);
        var comparison = filter.CompareFrom is { } compareFrom && filter.CompareTo is { } compareTo
            ? Window.Of(compareFrom, compareTo)
            : (Window?)null;

        var computed = new List<(TLoaded Loaded, Window Window, TRange Range)>();
        void Calculate(TLoaded loaded, Window window, bool isPrimary) =>
            computed.Add((loaded, window, calculate(loaded, window, isPrimary)));
        if (comparison is { } other && primary.Touches(other))
        {
            var both = await load(primary.Hull(other));
            Calculate(both, primary, true);
            Calculate(both, other, false);
        }
        else
        {
            Calculate(await load(primary), primary, true);
            if (comparison is { } separate)
                Calculate(await load(separate), separate, false);
        }

        List<string> names =
        [
            .. computed
                .SelectMany(c => c.Loaded.Sources.Profiles.Where(p => p.Start < c.Window.To && p.End > c.Window.From))
                .OrderBy(p => p.Start)
                .Select(p => p.ProfileName)
                .Distinct(StringComparer.Ordinal),
        ];
        return new Ranges<TRange>(computed[0].Range, computed.Count > 1 ? computed[1].Range : null, names);
    }

    private static LoopAnalysisMask Mask(
        LoopAnalysisFilter filter, Window window, TimeZoneInfo zone, LoopAnalysisMaskSources sources) =>
        LoopAnalysisMask.Build(filter.Masks, window.From, window.To, zone, sources);

    /// <summary>The reads the Daily profile and Loop health pages share, issued together.</summary>
    /// <remarks>
    /// Boluses are read from <see cref="DailyProfileCalculator.ActingMs"/> before the window so IOB
    /// and COB at its start see the doses still acting. Temp basals are read from
    /// <see cref="StatisticsService.TempBasalReadStart"/>, which reaches further back, and to
    /// <see cref="NetInsulinAboveSchedule.MaxInferredGapMs"/> past it, so a gap that straddles its end
    /// is bounded by the record that closes it. Overrides are read once, from the same start as temp
    /// basals, since they scale the schedule those are measured against (<see cref="ScheduledBasal"/>);
    /// the masks read the same spans.
    /// </remarks>
    private sealed record Common(
        IReadOnlyList<SensorGlucose> Glucose,
        List<Bolus> Boluses,
        List<TempBasal> TempBasals,
        Dictionary<StateSpanCategory, List<StateSpan>> Spans,
        IReadOnlyList<PatientDevice> Devices,
        DeviceDateClock DeviceDates,
        IReadOnlyList<ProfileSpan> Profiles,
        List<ProfileInterval> ProfileIntervals,
        List<ScaleFactorInterval> Overrides);

    private async Task<Common> LoadCommonAsync(Window window, CancellationToken ct)
    {
        var from = window.From;
        var to = window.To;
        var fromUtc = ToUtc(from);
        var toUtc = ToUtc(to);
        var lookbackUtc = ToUtc(from - DailyProfileCalculator.ActingMs);

        var glucoseTask = glucoseRepository.GetForAnalyticsAsync(fromUtc, toUtc, null, null, int.MaxValue, descending: false, ct: ct);
        var bolusTask = bolusRepository.GetAsync(lookbackUtc, toUtc, null, null, int.MaxValue, descending: false, ct: ct);
        var tempBasalTask = tempBasalRepository.GetAsync(
            StatisticsService.TempBasalReadStart(fromUtc), ToUtc(to + NetInsulinAboveSchedule.MaxInferredGapMs), null, null, int.MaxValue, descending: false, ct: ct);
        var spanTask = stateSpanRepository.GetByCategories(
            MaskCategories.Where(c => c != StateSpanCategory.Override), fromUtc, toUtc, ct);
        var deviceTask = patientDeviceRepository.GetByDateRangeAsync(fromUtc, toUtc, ct);
        var overrideTask = ScheduledBasal.ReadOverridesAsync(
            stateSpanRepository, StatisticsService.TempBasalReadStart(fromUtc).ToMills(), to, ct);
        await Task.WhenAll(glucoseTask, bolusTask, tempBasalTask, spanTask, deviceTask, overrideTask);

        var spans = await spanTask;
        var overrides = await overrideTask;
        spans[StateSpanCategory.Override] = overrides;
        var profiles = await activeProfileResolver.GetProfileSpansForRangeAsync(from, to, ct);
        var clock = await deviceDates.ResolveAsync(ct);
        return new Common(
            await canonicalGlucose.SelectAsync([.. await glucoseTask], ct),
            [.. await bolusTask],
            [.. await tempBasalTask],
            spans,
            [.. await deviceTask],
            clock,
            profiles,
            ProfileIntervals(profiles, to),
            [.. ScheduledBasal.Overrides(overrides, to)]);
    }

    private sealed record Delivery(
        NetInsulinAboveSchedule Insulin,
        BasalEvidence Evidence,
        IReadOnlyList<TempBasal> Temps,
        TherapyTimeline Timeline,
        ScheduledBasal Scheduled,
        long TickStart,
        bool HasTherapy);

    /// <summary>
    /// Basal delivered against <see cref="ScheduledBasal"/> and every bolus in the window, as
    /// <see cref="NetInsulinAboveSchedule"/> defines them, and the schedule the window's IOB is read
    /// against, which reaches back as far as the timeline. A temp basal still running stops at
    /// <paramref name="now"/>, or the window's end when that comes first: a padded window can end
    /// after now.
    /// </summary>
    private async Task<Delivery> DeliveryAsync(Common common, Window window, long now, CancellationToken ct)
    {
        var from = window.From;
        var to = window.To;
        var hasTherapy = await therapySettingsResolver.HasDataAsync(ct);
        var tickStart = from - from % TickMs;
        var actingFrom = tickStart - DailyProfileCalculator.ActingMs;
        var timeline = await therapyTimelineResolver.BuildAsync(actingFrom, to + 1, ct: ct);

        var (clipped, evidence) = NetInsulinAboveSchedule.ClipTempBasals(common.TempBasals, Math.Min(to, now));
        var scheduled = new ScheduledBasal(hasTherapy ? await ScheduleAsync(actingFrom, to, ct) : [], common.Overrides);
        var series = await basalSeriesBuilder.BuildAsync(
            clipped, tickStart, to, timeline.SnapshotAt(from).BasalRateAt(from), common.Overrides, ct);
        var delivered = NetInsulinAboveSchedule.DeliveredFromSeries(series, to, evidence);

        var doses = common.Boluses
            .Where(b => b.Mills >= from && b.Mills < to)
            .Select(b => new InsulinDose(b.Mills, b.Insulin, b.Kind == BolusKind.Algorithm, b.CorrelationId));

        return new Delivery(
            new NetInsulinAboveSchedule(delivered, scheduled.Segments(clipped), doses),
            evidence,
            [.. clipped.OrderBy(t => t.StartMills)],
            timeline,
            scheduled,
            tickStart,
            hasTherapy);
    }

    private static List<ProfileInterval> ProfileIntervals(IEnumerable<ProfileSpan> profiles, long to) =>
        [.. profiles.Select(p => new ProfileInterval(p.ProfileName, p.StartMills, p.EndMills ?? to))];

    /// <summary>
    /// Each profile's stored versions, newest first, read once per profile name, for
    /// <see cref="ProfileVersionCache.ActiveAt"/>.
    /// </summary>
    private static async Task<Dictionary<string, T[]>> VersionsByProfileAsync<T>(
        IEnumerable<ProfileSpan> profiles, IProfileScopedRepository<T> repository, CancellationToken ct)
        where T : class, IV4Record, IProfileScoped
    {
        var versions = new Dictionary<string, T[]>(StringComparer.Ordinal);
        foreach (var name in profiles.Select(p => p.ProfileName).Distinct(StringComparer.Ordinal))
            versions[name] = [.. (await repository.GetByProfileNameAsync(name, ct)).OrderByDescending(v => v.Timestamp)];
        return versions;
    }

    private async Task<List<BasalSegment>> ScheduleAsync(long from, long to, CancellationToken ct)
    {
        var segments = new List<BasalSegment>();
        await foreach (var segment in basalSegmentService.GetSegmentsAsync(from, to, ct))
            segments.Add(segment);
        return segments;
    }

    /// <summary>
    /// When each APS snapshot was stored, and whether its loop was closed: the snapshot evidence of
    /// the open-loop mask (<see cref="LoopAnalysisMask.OpenLoop"/>) on every page. A snapshot shows
    /// the loop ran, whether or not it enacted anything.
    /// </summary>
    internal static List<ApsLoopState> LoopStates(IEnumerable<ApsIobCobPoint> snapshots) =>
        [.. snapshots.Select(s => new ApsLoopState(s.Timestamp.ToMills(), ApsLoopCycle.IsLoopClosed(s.ClosedLoop)))];

    /// <inheritdoc cref="LoopStates(IEnumerable{ApsIobCobPoint})"/>
    internal static List<ApsLoopState> LoopStates(IEnumerable<ApsLoopCycle> cycles) =>
        [.. cycles.Select(c => new ApsLoopState(c.Mills, c.LoopClosed))];

    /// <summary><see cref="MaskSources"/> over <paramref name="common"/>'s spans, devices and profiles.</summary>
    private static LoopAnalysisMaskSources Sources(
        Common common, IReadOnlyList<ApsLoopState> snapshots, long openEnd) =>
        MaskSources(common.Spans, common.Devices, common.DeviceDates, snapshots, common.ProfileIntervals, openEnd);

    /// <param name="openEnd">Where a span still open ends: the end of the time its readers look at.</param>
    private static LoopAnalysisMaskSources MaskSources(
        Dictionary<StateSpanCategory, List<StateSpan>> spans,
        IEnumerable<PatientDevice> devices,
        DeviceDateClock deviceDates,
        IReadOnlyList<ApsLoopState> snapshots,
        IReadOnlyList<ProfileInterval> profiles,
        long openEnd) => new()
    {
        Overrides = [.. SpansOf(spans, StateSpanCategory.Override, openEnd).Select(s => s.Interval)],
        TemporaryTargets = [.. ActiveTemporaryTargets(spans, openEnd).Select(s => s.Interval)],
        Exercise = [.. SpansOf(spans, StateSpanCategory.Exercise, openEnd).Select(s => s.Interval)],
        Illness = [.. SpansOf(spans, StateSpanCategory.Illness, openEnd).Select(s => s.Interval)],
        DataExclusions = [.. SpansOf(spans, StateSpanCategory.DataExclusion, openEnd).Select(s => s.Interval)],
        OpenLoop = OpenLoop(spans, devices, deviceDates, snapshots, openEnd).Intervals,
        Profiles = profiles,
    };

    private static List<(StateSpan Span, MillsInterval Interval)> ActiveTemporaryTargets(
        Dictionary<StateSpanCategory, List<StateSpan>> spans, long openEnd) =>
        [.. SpansOf(spans, StateSpanCategory.TemporaryTarget, openEnd)
            .Where(s => s.Span.State != nameof(TemporaryTargetState.Cancelled))];

    /// <summary>
    /// <see cref="LoopAnalysisMask.OpenLoop"/> from the pump mode spans and the registered pumps. A
    /// pump whose algorithm is <see cref="AidAlgorithm.Unknown"/> is neither: it says nothing about
    /// whether the loop was closed.
    /// </summary>
    private static IntervalSet OpenLoop(
        Dictionary<StateSpanCategory, List<StateSpan>> spans,
        IEnumerable<PatientDevice> devices,
        DeviceDateClock deviceDates,
        IReadOnlyList<ApsLoopState> snapshots,
        long to)
    {
        var pumps = devices
            .Where(d => d.DeviceCategory == DeviceCategory.InsulinPump && d.AidAlgorithm is { } algorithm && algorithm != AidAlgorithm.Unknown)
            .ToList();
        MillsInterval Window(PatientDevice pump) => deviceDates.Window(pump.StartDate, pump.EndDate, to);

        return LoopAnalysisMask.OpenLoop(
            SpansOf(spans, StateSpanCategory.PumpMode, to)
                .Where(s => s.Span.State is nameof(PumpModeState.Manual) or nameof(PumpModeState.Off))
                .Select(s => s.Interval),
            pumps.Where(p => p.AidAlgorithm == AidAlgorithm.None).Select(Window),
            pumps.Where(p => p.AidAlgorithm != AidAlgorithm.None).Select(Window),
            snapshots);
    }

    private static List<(StateSpan Span, MillsInterval Interval)> SpansOf(
        Dictionary<StateSpanCategory, List<StateSpan>> spans, StateSpanCategory category, long to) =>
        spans.TryGetValue(category, out var list)
            ? [.. list.Select(s => (s, new MillsInterval(s.StartMills, s.EndMills ?? to)))]
            : [];

    private static DateTime ToUtc(long mills) => DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;

    private readonly record struct Window(long From, long To)
    {
        public static Window Of(DateTime from, DateTime to) =>
            to > from
                ? new(from.ToMills(), to.ToMills())
                : throw new ArgumentOutOfRangeException(nameof(to), "A Loop Analysis range must end after it starts.");

        public bool Touches(Window other) => From <= other.To && other.From <= To;

        public Window Hull(Window other) => new(Math.Min(From, other.From), Math.Max(To, other.To));
    }
}
