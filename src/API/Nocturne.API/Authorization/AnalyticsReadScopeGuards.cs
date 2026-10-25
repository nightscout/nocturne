using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.Widget;

namespace Nocturne.API.Authorization;

/// <summary>
/// Per-category read scopes for the V4 analytics payloads that merge several data categories into
/// one response, the shape <see cref="ActogramReadScopeGuard"/> documents: the controller attribute
/// lists the categories as an OR so any caller holding one of them is admitted, and the guard
/// empties the categories the caller does not hold. Attribute arguments must be compile-time
/// constants, so each attribute repeats its admission list inline and the <c>AdmissionScopes</c>
/// members here are what the guard tests pin it against.
/// </summary>
internal static class AnalyticsReadScopes
{
    /// <summary>
    /// The two categories the retrospective, prediction and correlation payloads span. Glucose
    /// covers the sensor/meter/calibration/BG-check readings and everything derived from them;
    /// treatments covers insulin, carbs and the basal and IOB/COB series computed from them.
    /// </summary>
    public static readonly IReadOnlyList<string> GlucoseAndTreatments =
    [
        Scope.GlucoseRead,
        Scope.TreatmentsRead,
    ];

    public static bool Allows(IReadOnlySet<string> grantedScopes, string scope) =>
        Scope.Satisfies(grantedScopes, scope);
}

/// <summary>
/// Per-category read scopes for the dashboard chart, the widest merge in the API: glucose readings
/// and their threshold band, the treatment markers and the IOB/COB/basal series derived from them,
/// device and system events, profile switches, and the heart-rate and step overlays.
/// </summary>
/// <seealso cref="AnalyticsReadScopes"/>
internal static class ChartDataReadScopeGuard
{
    public static readonly IReadOnlyList<string> AdmissionScopes =
    [
        Scope.GlucoseRead,
        Scope.TreatmentsRead,
        Scope.DevicesRead,
        Scope.TherapyRead,
        Scope.HeartRateRead,
        Scope.StepCountRead,
        Scope.SleepRead,
    ];

    public static DashboardChartData Redact(
        DashboardChartData data,
        IReadOnlySet<string> grantedScopes)
    {
        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.GlucoseRead))
        {
            // Thresholds are the band edges of the glucose series, so they leave with it.
            data.GlucoseData = [];
            data.Thresholds = new ChartThresholdsDto();
            data.BgCheckMarkers = [];
        }

        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.TreatmentsRead))
        {
            data.IobSeries = [];
            data.CobSeries = [];
            data.BasalSeries = [];
            data.BolusMarkers = [];
            data.CarbMarkers = [];
            data.BasalInjectionMarkers = [];
            data.OverrideSpans = [];
            data.TempBasalSpans = [];
            data.BasalDeliverySpans = [];
            data.DefaultBasalRate = 0;
            data.MaxBasalRate = 0;
            data.MinIob = 0;
            data.MaxIob = 0;
            data.MaxCob = 0;
        }

        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.DevicesRead))
        {
            data.DeviceEventMarkers = [];
            data.SystemEventMarkers = [];
            data.PumpModeSpans = [];
        }

        // Read off both the basal delivery spans and the pump status, so it needs both scopes.
        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.TreatmentsRead)
            || !AnalyticsReadScopes.Allows(grantedScopes, Scope.DevicesRead))
            data.LastPumpSyncMills = null;

        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.TherapyRead))
            data.ProfileSpans = [];

        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.HeartRateRead))
            data.HeartRateSeries = [];

        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.StepCountRead))
            data.StepSeries = [];

        // ActivitySpans is the one field with two governing categories: the exercise, illness and
        // travel state spans are treatment annotations, while the sleep sessions projected in
        // beside them are their own category, so it is filtered per span rather than emptied.
        data.ActivitySpans = [.. data.ActivitySpans.Where(s => AnalyticsReadScopes.Allows(
            grantedScopes,
            s.Kind == ChartSpanKind.Sleep ? Scope.SleepRead : Scope.TreatmentsRead))];

        return data;
    }
}

/// <summary>
/// Per-category read scopes for the retrospective "what was happening at this moment" payloads:
/// the glucose value and direction on one side, the IOB/COB/basal state and the recent treatments
/// that produced it on the other.
/// </summary>
/// <seealso cref="AnalyticsReadScopes"/>
internal static class RetrospectiveReadScopeGuard
{
    public static readonly IReadOnlyList<string> AdmissionScopes = AnalyticsReadScopes.GlucoseAndTreatments;

    public static RetrospectiveDataResponse Redact(
        RetrospectiveDataResponse data,
        IReadOnlySet<string> grantedScopes)
    {
        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.GlucoseRead))
            data.Glucose = null;

        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.TreatmentsRead))
        {
            data.Iob = null;
            data.Cob = null;
            data.Basal = null;
            data.RecentTreatments = [];
        }

        return data;
    }

    public static RetrospectiveTimelineResponse Redact(
        RetrospectiveTimelineResponse data,
        IReadOnlySet<string> grantedScopes)
    {
        var glucose = AnalyticsReadScopes.Allows(grantedScopes, Scope.GlucoseRead);
        var treatments = AnalyticsReadScopes.Allows(grantedScopes, Scope.TreatmentsRead);

        if (glucose && treatments)
            return data;

        // The timeline interleaves both categories in every point, so redaction is per field
        // rather than per collection; the frontend already renders a point with a null glucose.
        foreach (var point in data.Data ?? [])
        {
            if (!glucose)
            {
                point.Glucose = null;
                point.GlucoseDirection = null;
            }

            if (!treatments)
            {
                point.Iob = 0;
                point.BolusIob = 0;
                point.BasalIob = 0;
                point.Cob = 0;
                point.BasalRate = 0;
                point.IsTemp = false;
            }
        }

        return data;
    }
}

/// <summary>
/// Per-category read scopes for the glucose forecast: the curves and the current/eventual glucose
/// they run from are the glucose category, while the insulin and carb state the forecast was
/// computed against is the treatment category.
/// </summary>
/// <seealso cref="AnalyticsReadScopes"/>
internal static class PredictionReadScopeGuard
{
    public static readonly IReadOnlyList<string> AdmissionScopes = AnalyticsReadScopes.GlucoseAndTreatments;

    public static GlucosePredictionResponse Redact(
        GlucosePredictionResponse data,
        IReadOnlySet<string> grantedScopes)
    {
        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.GlucoseRead))
        {
            data.CurrentBg = 0;
            data.Delta = 0;
            data.EventualBg = 0;
            data.Predictions = new PredictionCurves();
        }

        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.TreatmentsRead))
        {
            data.Iob = 0;
            data.Cob = 0;
            data.SensitivityRatio = null;
        }

        return data;
    }
}

/// <summary>
/// Per-category read scopes for the correlation lookup, which fans one correlation id out across
/// the glucose repositories (sensor, meter, calibration, BG check) and the treatment repositories
/// (bolus, carb intake, note, bolus calculation).
/// </summary>
/// <remarks>
/// Redaction here is a decision not to query rather than a decision to empty: the controller skips
/// the repositories whose category the caller does not hold, so a partial read costs less than a
/// full one.
/// </remarks>
/// <seealso cref="AnalyticsReadScopes"/>
internal static class CorrelationReadScopeGuard
{
    public static readonly IReadOnlyList<string> AdmissionScopes = AnalyticsReadScopes.GlucoseAndTreatments;

    public static bool AllowsGlucose(IReadOnlySet<string> grantedScopes) =>
        AnalyticsReadScopes.Allows(grantedScopes, Scope.GlucoseRead);

    public static bool AllowsTreatments(IReadOnlySet<string> grantedScopes) =>
        AnalyticsReadScopes.Allows(grantedScopes, Scope.TreatmentsRead);
}

/// <summary>
/// Per-category read scopes for the widget summary, which packs the current and historical glucose,
/// the IOB/COB state, the tracker statuses and the firing alarm into one small payload.
/// </summary>
/// <remarks>
/// Trackers carry no <see cref="ShareDataCategories"/> category; the controller drops them by
/// <see cref="TrackerReadAccess"/> instead.
/// </remarks>
/// <seealso cref="AnalyticsReadScopes"/>
internal static class WidgetSummaryReadScopeGuard
{
    public static readonly IReadOnlyList<string> AdmissionScopes =
    [
        Scope.GlucoseRead,
        Scope.TreatmentsRead,
        Scope.AlertsRead,
    ];

    public static V4SummaryResponse Redact(
        V4SummaryResponse data,
        IReadOnlySet<string> grantedScopes)
    {
        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.GlucoseRead))
        {
            data.Current = null;
            data.History = [];
            data.Predictions = null;
        }

        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.TreatmentsRead))
        {
            data.Iob = 0;
            data.Cob = 0;
        }

        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.AlertsRead))
            data.Alarm = null;

        return data;
    }
}

/// <summary>
/// Per-category read scopes for the "right now" therapy state: the pump mode and the pump's
/// reservoir and battery readings are the device category, the sensitivity adjustment is read off the
/// active therapy profile, and the thresholds follow glucose as in <see cref="ChartDataReadScopeGuard"/>.
/// Glucose alone admits the caller because the dashboard sparkline draws its band from these
/// thresholds for any viewer of the glucose series.
/// </summary>
/// <seealso cref="AnalyticsReadScopes"/>
internal static class CurrentTherapyStateReadScopeGuard
{
    public static readonly IReadOnlyList<string> AdmissionScopes =
    [
        Scope.GlucoseRead,
        Scope.DevicesRead,
        Scope.TherapyRead,
    ];

    public static CurrentTherapyStateResponse Redact(
        CurrentTherapyStateResponse data,
        IReadOnlySet<string> grantedScopes)
    {
        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.DevicesRead))
        {
            data.CurrentPumpMode = null;
            data.Reservoir = null;
            data.PumpBatteryPercent = null;
            data.PumpBatteryVoltage = null;
        }

        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.GlucoseRead))
            data.Thresholds = new ChartThresholdsDto();

        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.TherapyRead))
            data.SensitivityPercent = null;

        return data;
    }
}

/// <summary>
/// Per-category read scopes for <c>GET /periods</c> and <c>GET /punch-card</c>, admitted on
/// <c>glucose.read</c> or <c>reports.read</c>, which also summarise insulin and carbs and count
/// sensor sessions from device events. Category row level security hides those categories only
/// from shares, so for a member, guest, OAuth client or token the narrowing has to happen here.
/// </summary>
/// <remarks>
/// Insulin and carb aggregates need <c>reports.read</c> or <c>treatments.read</c>; a list of
/// individual treatment records needs <c>treatments.read</c>, as <c>insulin-delivery-stats</c> does
/// for <c>ManualBoluses</c>. A share without the treatments category has no rows to aggregate.
/// <c>GET /periods</c> is served from <see cref="Nocturne.API.Services.Caching.ITenantCache"/>,
/// whose value holds every category; <see cref="Redact"/> narrows a copy and never the cached
/// instance. The treatment and device narrowings are independent.
/// </remarks>
/// <seealso cref="AnalyticsReadScopes"/>
internal static class StatisticsReadScopeGuard
{
    public static readonly IReadOnlyList<string> AdmissionScopes =
    [
        Scope.GlucoseRead,
        Scope.ReportsRead,
    ];

    public static bool AllowsTreatmentAggregates(IReadOnlySet<string> grantedScopes) =>
        AnalyticsReadScopes.Allows(grantedScopes, Scope.ReportsRead)
        || AnalyticsReadScopes.Allows(grantedScopes, Scope.TreatmentsRead);

    public static bool AllowsDevices(IReadOnlySet<string> grantedScopes) =>
        AnalyticsReadScopes.Allows(grantedScopes, Scope.DevicesRead);

    public static MultiPeriodStatistics Redact(
        MultiPeriodStatistics data,
        IReadOnlySet<string> grantedScopes)
    {
        var treatments = AllowsTreatmentAggregates(grantedScopes);
        var devices = AllowsDevices(grantedScopes);

        if (treatments && devices)
            return data;

        return new MultiPeriodStatistics
        {
            LastDay = Narrow(data.LastDay, treatments, devices),
            Last3Days = Narrow(data.Last3Days, treatments, devices),
            LastWeek = Narrow(data.LastWeek, treatments, devices),
            LastMonth = Narrow(data.LastMonth, treatments, devices),
            Last90Days = Narrow(data.Last90Days, treatments, devices),
            LastUpdated = data.LastUpdated,
        };
    }

    private static PeriodStatistics Narrow(PeriodStatistics period, bool treatments, bool devices) => new()
    {
        PeriodDays = period.PeriodDays,
        StartDate = period.StartDate,
        EndDate = period.EndDate,
        Analytics = devices ? period.Analytics : WithoutSensorWarmups(period.Analytics),
        TreatmentSummary = treatments ? period.TreatmentSummary : null,
        InsulinDelivery = treatments ? period.InsulinDelivery : null,
        HasSufficientData = period.HasSufficientData,
        Gmi = period.Gmi,
        Reliability = period.Reliability,
        EntryCount = period.EntryCount,
        TreatmentCount = treatments ? period.TreatmentCount : 0,
    };

    private static GlucoseAnalytics? WithoutSensorWarmups(GlucoseAnalytics? analytics) =>
        analytics is null
            ? null
            : new GlucoseAnalytics
            {
                BasicStats = analytics.BasicStats,
                TimeInRange = analytics.TimeInRange,
                GlycemicVariability = analytics.GlycemicVariability,
                DataQuality = new DataQuality
                {
                    TotalReadings = analytics.DataQuality.TotalReadings,
                    MissingReadings = analytics.DataQuality.MissingReadings,
                    DataCompleteness = analytics.DataQuality.DataCompleteness,
                    CgmActivePercent = analytics.DataQuality.CgmActivePercent,
                    GapAnalysis = analytics.DataQuality.GapAnalysis,
                    NoiseLevel = analytics.DataQuality.NoiseLevel,
                    CalibrationEvents = analytics.DataQuality.CalibrationEvents,
                    SensorWarmups = 0,
                },
                Reliability = analytics.Reliability,
                Time = analytics.Time,
                ClinicalAssessment = analytics.ClinicalAssessment,
                TimeZoneUnavailableReason = analytics.TimeZoneUnavailableReason,
            };
}

/// <summary>
/// Per-category read scopes for <c>GET /api/v4/statistics/aid-system-metrics</c>, admitted on
/// <c>reports.read</c>. The CGM active time and the AID and pump shares, with their segments, are
/// withheld as Loop health withholds the same figures
/// (<see cref="LoopAnalysisReadScopeGuard.WithheldLoopHealth"/>). The target range is a therapy
/// setting. The site changes and the registered device names are devices.
/// </summary>
/// <remarks>The response is built per request and never cached server-side, so it is narrowed in place.</remarks>
/// <seealso cref="AnalyticsReadScopes"/>
internal static class AidSystemMetricsReadScopeGuard
{
    public static AidSystemMetrics Redact(AidSystemMetrics data, IReadOnlySet<string> grantedScopes)
    {
        var loopHealth = LoopAnalysisReadScopeGuard.WithheldLoopHealth(grantedScopes);
        var devices = AnalyticsReadScopes.Allows(grantedScopes, Scope.DevicesRead);
        var therapy = AnalyticsReadScopes.Allows(grantedScopes, Scope.TherapyRead);

        if (loopHealth.Contains(LoopAnalysisSeries.CgmActivity))
            data.CgmActivePercent = null;
        if (loopHealth.Contains(LoopAnalysisSeries.LoopActivity))
        {
            data.AidActivePercent = null;
            data.PumpUsePercent = null;
            data.Segments = [];
        }
        if (!therapy)
        {
            data.TargetLow = null;
            data.TargetHigh = null;
        }
        if (!devices)
        {
            data.SiteChangeCount = null;
            data.CgmDeviceNames = null;
            data.PumpDeviceNames = null;
        }
        return data;
    }
}

/// <summary>
/// Per-category read scopes for Loop Analysis, admitted on <c>reports.read</c>: glucose is the
/// glucose category; delivered basal, the insulin above schedule, IOB/COB, boluses and carbs are
/// treatments; the profile names and the separate schedule series (scheduled basal, target range)
/// are therapy settings. Both of those series also carry treatments (the scheduled basal is scaled
/// by any override, the target range overlays temporary targets), so they need treatments too.
/// The masks follow the categories of the spans they are drawn from, as
/// <see cref="ChartDataReadScopeGuard"/> serves them: overrides, temporary targets, exercise and
/// illness are treatments, pump mode and the registered pumps are devices.
/// </summary>
/// <remarks>
/// <para>
/// The schedule can be worked back from the delivered basal and the components of insulin above
/// schedule. That is not a disclosure past <c>treatments.read</c>: a schedule value carried inside
/// delivery is treatment data, as <see cref="ChartDataReadScopeGuard"/> serves the basal series and
/// its scheduled rate under that scope. <c>therapy.read</c> gates the schedule and target series
/// served in their own right, and the profile names.
/// </para>
/// <para>
/// Loop health reads the APS loop cycles, which are devices: the loop gaps, the <c>max_iob</c>
/// share, the suggestions and the sensitivity ratio. Zero delivery and the automatic boluses are
/// treatments; the closed-loop and pump shares, and the unrecorded delivery gaps, draw on both.
/// The limits are therapy settings, and oref's are reported by the cycles. Time at the maximum
/// basal rate is measured on delivered basal against the cap the cycles say was in force, and for
/// Loop the hours at the cap show where its setting sits, so it needs all three.
/// </para>
/// <para>The response is built per request and never cached server-side, so it is narrowed in place.</para>
/// </remarks>
/// <seealso cref="AnalyticsReadScopes"/>
internal static partial class LoopAnalysisReadScopeGuard
{
    public const string AdmissionScope = Scope.ReportsRead;

    /// <summary>
    /// Drops from <paramref name="masks"/> every part the caller could not otherwise see: a profile
    /// name without <c>therapy.read</c>, and each mask drawn from a category the caller lacks, which
    /// would otherwise show where those spans fell through the analysed time.
    /// </summary>
    /// <returns>The query parameter names that were ignored, for the response to report.</returns>
    public static List<string> NarrowFilter(LoopAnalysisMasks masks, IReadOnlySet<string> grantedScopes)
    {
        var ignored = new List<string>();
        foreach (var rule in FilterRules)
        {
            if (AnalyticsReadScopes.Allows(grantedScopes, rule.Scope) || !rule.IsSet(masks))
                continue;
            rule.Clear(masks);
            ignored.Add(rule.Name);
        }
        return ignored;
    }

    /// <summary>
    /// Every filter <see cref="NarrowFilter"/> would drop for this caller, set or not, with the
    /// scope it needs.
    /// </summary>
    public static List<LoopAnalysisUnavailableFilter> UnavailableFilters(IReadOnlySet<string> grantedScopes) =>
    [
        .. FilterRules
            .Where(rule => !AnalyticsReadScopes.Allows(grantedScopes, rule.Scope))
            .Select(rule => new LoopAnalysisUnavailableFilter { Filter = rule.Name, RequiredScope = rule.Scope }),
    ];

    /// <summary>The query parameter of every filter the guard narrows.</summary>
    public static IEnumerable<string> FilterNames => FilterRules.Select(rule => rule.Name);

    /// <summary>Each filter by its query parameter, the scope it is drawn from, and how to drop it.</summary>
    private sealed record FilterRule(
        string Name, string Scope, Func<LoopAnalysisMasks, bool> IsSet, Action<LoopAnalysisMasks> Clear);

    private static readonly FilterRule[] FilterRules =
    [
        new("profileName", Scope.TherapyRead, m => m.ProfileName is not null, m => m.ProfileName = null),
        new("excludeOverrides", Scope.TreatmentsRead, m => m.ExcludeOverrides, m => m.ExcludeOverrides = false),
        new("excludeTemporaryTargets", Scope.TreatmentsRead, m => m.ExcludeTemporaryTargets, m => m.ExcludeTemporaryTargets = false),
        new("excludeExercise", Scope.TreatmentsRead, m => m.ExcludeExercise, m => m.ExcludeExercise = false),
        new("excludeIllness", Scope.TreatmentsRead, m => m.ExcludeIllness, m => m.ExcludeIllness = false),
        new("excludeOpenLoop", Scope.DevicesRead, m => m.ExcludeOpenLoop, m => m.ExcludeOpenLoop = false),
    ];

    /// <summary>
    /// The Daily profile series the caller's scopes withhold. <see cref="Redact{T}"/> empties exactly these, so the
    /// response's <see cref="LoopAnalysisResponse.WithheldSeries"/> and its nulls cannot disagree.
    /// </summary>
    public static HashSet<LoopAnalysisSeries> WithheldDailyProfile(IReadOnlySet<string> grantedScopes)
    {
        var glucose = AnalyticsReadScopes.Allows(grantedScopes, Scope.GlucoseRead);
        var treatments = AnalyticsReadScopes.Allows(grantedScopes, Scope.TreatmentsRead);
        var therapy = AnalyticsReadScopes.Allows(grantedScopes, Scope.TherapyRead);

        var withheld = new HashSet<LoopAnalysisSeries>();
        Withhold(withheld, !glucose, LoopAnalysisSeries.Glucose);
        Withhold(withheld, !treatments,
            LoopAnalysisSeries.DeliveredBasal,
            LoopAnalysisSeries.NetLoopCorrection,
            LoopAnalysisSeries.NetLoopCorrectionComponents,
            LoopAnalysisSeries.HourlyNetLoopCorrection,
            LoopAnalysisSeries.Iob,
            LoopAnalysisSeries.Cob,
            LoopAnalysisSeries.ManualBoluses,
            LoopAnalysisSeries.Carbs,
            LoopAnalysisSeries.InsulinSummary,
            LoopAnalysisSeries.NetLoopCorrectionSummary);
        Withhold(withheld, !therapy || !treatments,
            LoopAnalysisSeries.ScheduledBasal,
            LoopAnalysisSeries.Target,
            LoopAnalysisSeries.DeliveredToScheduledRatio);
        return withheld;
    }

    /// <summary>A page: the series the caller's scopes withhold, and how its response drops exactly those.</summary>
    private sealed record Page(
        Func<IReadOnlySet<string>, HashSet<LoopAnalysisSeries>> Withheld,
        Action<LoopAnalysisResponse, HashSet<LoopAnalysisSeries>> Narrow);

    /// <summary>
    /// Every page, by its response type, and the envelope served on its own, which carries no
    /// series. Every <see cref="LoopAnalysisSeries"/> belongs to exactly one page or to the envelope
    /// (<see cref="WithheldEnvelope"/>), so a page's response never lists another's series; a new
    /// page adds one entry here.
    /// </summary>
    private static readonly Dictionary<Type, Page> Pages = new()
    {
        [typeof(LoopAnalysisResponse)] = new(_ => [], (_, _) => { }),
        [typeof(DailyProfileResponse)] = PageOf<DailyProfileResponse, DailyProfileRange>(
            WithheldDailyProfile, r => [r.Primary, r.Comparison], Narrow),
        [typeof(PredictionAccuracyResponse)] = PageOf<PredictionAccuracyResponse, PredictionAccuracyRange>(
            WithheldPredictions, r => [r.Primary, r.Comparison], Narrow),
        [typeof(LoopHealthResponse)] = PageOf<LoopHealthResponse, LoopHealthRange>(
            WithheldLoopHealth, r => [r.Primary, r.Comparison], Narrow),
        [typeof(EventResponseReport)] = PageOf<EventResponseReport, EventResponseRange>(
            WithheldEvents, r => [r.Primary, r.Comparison], Narrow),
        [typeof(SettingsEvidenceReport)] = new(WithheldSettingsEvidence, NarrowSettingsEvidence),
    };

    private static Page PageOf<TResponse, TRange>(
        Func<IReadOnlySet<string>, HashSet<LoopAnalysisSeries>> withheld,
        Func<TResponse, TRange?[]> ranges,
        Action<TRange, HashSet<LoopAnalysisSeries>> narrow)
        where TResponse : LoopAnalysisResponse
        where TRange : class =>
        new(withheld, (response, hidden) =>
        {
            foreach (var range in ranges((TResponse)response))
                if (range is not null)
                    narrow(range, hidden);
        });

    public static IReadOnlyCollection<Type> PageResponses => Pages.Keys;

    public static HashSet<LoopAnalysisSeries> WithheldFrom(Type response, IReadOnlySet<string> grantedScopes) =>
        Pages[response].Withheld(grantedScopes);

    /// <summary>
    /// Lists the series the caller's scopes withhold from <typeparamref name="T"/>'s page and empties
    /// exactly those.
    /// </summary>
    public static T Redact<T>(T data, IReadOnlySet<string> grantedScopes) where T : LoopAnalysisResponse
    {
        var page = Pages[typeof(T)];
        var withheld = page.Withheld(grantedScopes);
        withheld.UnionWith(WithheldEnvelope(grantedScopes));
        data.WithheldSeries = [.. withheld.Order()];
        if (withheld.Contains(LoopAnalysisSeries.ProfileNames))
            data.ProfileNames = [];
        if (withheld.Count > 0)
            page.Narrow(data, withheld);
        return data;
    }

    /// <summary>
    /// The series of the envelope every page carries, which <see cref="Redact{T}"/> withholds whatever
    /// the page: the profile names are therapy settings.
    /// </summary>
    public static HashSet<LoopAnalysisSeries> WithheldEnvelope(IReadOnlySet<string> grantedScopes)
    {
        var withheld = new HashSet<LoopAnalysisSeries>();
        Withhold(withheld, !AnalyticsReadScopes.Allows(grantedScopes, Scope.TherapyRead), LoopAnalysisSeries.ProfileNames);
        return withheld;
    }

    /// <summary>
    /// The prediction figures the caller's scopes withhold, as <see cref="WithheldDailyProfile"/> is for the
    /// Daily profile. A curve's accuracy compares the APS snapshots, which are devices, with the
    /// sensor readings, which are glucose, so it needs both. Its context and its interrupted counts
    /// are drawn from treatments.
    /// </summary>
    /// <remarks>
    /// Without treatments a curve kept for every context still leaves out its interrupted values, so
    /// a lower n in an hour hints weakly at when meals and corrections happen. That residue is
    /// accepted: the counts themselves, which would say it outright, are withheld.
    /// </remarks>
    public static HashSet<LoopAnalysisSeries> WithheldPredictions(IReadOnlySet<string> grantedScopes)
    {
        var glucose = AnalyticsReadScopes.Allows(grantedScopes, Scope.GlucoseRead);
        var devices = AnalyticsReadScopes.Allows(grantedScopes, Scope.DevicesRead);
        var treatments = AnalyticsReadScopes.Allows(grantedScopes, Scope.TreatmentsRead);

        var withheld = new HashSet<LoopAnalysisSeries>();
        Withhold(withheld, !devices, LoopAnalysisSeries.PredictionCount);
        Withhold(withheld, !glucose || !devices, LoopAnalysisSeries.PredictionCurves);
        Withhold(withheld, !treatments, LoopAnalysisSeries.PredictionContexts);
        return withheld;
    }

    private static void Withhold(HashSet<LoopAnalysisSeries> withheld, bool hidden, params LoopAnalysisSeries[] series)
    {
        if (hidden)
            withheld.UnionWith(series);
    }

    private static void Narrow(PredictionAccuracyRange range, HashSet<LoopAnalysisSeries> withheld)
    {
        if (withheld.Contains(LoopAnalysisSeries.PredictionCount))
        {
            range.Predictions = 0;
            range.Algorithms = [];
        }

        if (withheld.Contains(LoopAnalysisSeries.PredictionCurves))
        {
            range.Curves = [];
            range.HiddenCurves = [];
        }

        if (withheld.Contains(LoopAnalysisSeries.PredictionContexts))
        {
            var everyContext = Enum.GetValues<PredictionContext>().Length;
            // From the algorithms, not the curves carried: oref uploads its COB curve only while carbs
            // are absorbing, so which partial curves a range carried would say when carbs were on board.
            // With every curve withheld none is hidden for want of treatments alone.
            if (!withheld.Contains(LoopAnalysisSeries.PredictionCurves))
                range.HiddenCurves =
                [
                    .. range.Algorithms
                        .SelectMany(PredictionAccuracyCalculator.CurvesOf)
                        .Where(PredictionAccuracyCalculator.IsReportedInSomeContexts)
                        .Distinct()
                        .Order(),
                ];
            range.Curves.RemoveAll(c => c.Contexts.Count < everyContext);
            foreach (var curve in range.Curves)
            {
                curve.ByContext = [];
                foreach (var horizon in curve.Horizons.Concat(curve.ByHour.SelectMany(h => h.Horizons)))
                    horizon.Interrupted = null;
            }
        }
    }

    /// <summary>
    /// The Loop health figures the caller's scopes withhold, as <see cref="WithheldDailyProfile"/> is for the
    /// Daily profile.
    /// </summary>
    public static HashSet<LoopAnalysisSeries> WithheldLoopHealth(IReadOnlySet<string> grantedScopes)
    {
        var glucose = AnalyticsReadScopes.Allows(grantedScopes, Scope.GlucoseRead);
        var treatments = AnalyticsReadScopes.Allows(grantedScopes, Scope.TreatmentsRead);
        var devices = AnalyticsReadScopes.Allows(grantedScopes, Scope.DevicesRead);
        var therapy = AnalyticsReadScopes.Allows(grantedScopes, Scope.TherapyRead);

        var withheld = new HashSet<LoopAnalysisSeries>();
        Withhold(withheld, !glucose, LoopAnalysisSeries.CgmActivity);
        Withhold(withheld, !treatments || !devices, LoopAnalysisSeries.LoopActivity, LoopAnalysisSeries.UnrecordedDelivery);
        Withhold(withheld, !treatments, LoopAnalysisSeries.ZeroDelivery, LoopAnalysisSeries.AutomaticBoluses);
        Withhold(withheld, !devices || !treatments || !therapy, LoopAnalysisSeries.MaxBasal);
        Withhold(withheld, !devices,
            LoopAnalysisSeries.LoopGaps,
            LoopAnalysisSeries.AtMaxIob,
            LoopAnalysisSeries.SuggestedNotEnacted,
            LoopAnalysisSeries.SensitivityRatio);
        Withhold(withheld, !therapy || !devices, LoopAnalysisSeries.LoopLimits);
        Withhold(withheld, !therapy || !devices, LoopAnalysisSeries.OrefMaxIob);
        return withheld;
    }

    /// <summary>
    /// The Settings evidence the caller's scopes withhold: all of it, or none. Every figure combines
    /// glucose, the insulin delivered and the profile's settings, and its episodes place each bolus
    /// and meal in time, so no part of it stands without all three.
    /// </summary>
    public static HashSet<LoopAnalysisSeries> WithheldSettingsEvidence(IReadOnlySet<string> grantedScopes)
    {
        var withheld = new HashSet<LoopAnalysisSeries>();
        Withhold(
            withheld,
            !AnalyticsReadScopes.Allows(grantedScopes, Scope.GlucoseRead)
                || !AnalyticsReadScopes.Allows(grantedScopes, Scope.TreatmentsRead)
                || !AnalyticsReadScopes.Allows(grantedScopes, Scope.TherapyRead),
            LoopAnalysisSeries.SettingsEvidence);
        return withheld;
    }

    private static void NarrowSettingsEvidence(LoopAnalysisResponse response, HashSet<LoopAnalysisSeries> withheld)
    {
        if (!withheld.Contains(LoopAnalysisSeries.SettingsEvidence))
            return;
        var report = (SettingsEvidenceReport)response;
        report.Primary = null;
        report.Comparison = null;
    }

    private static void Narrow(LoopHealthRange range, HashSet<LoopAnalysisSeries> withheld)
    {
        bool Hidden(LoopAnalysisSeries series) => withheld.Contains(series);

        if (Hidden(LoopAnalysisSeries.CgmActivity))
        {
            range.Activity.Cgm = null;
            range.Activity.CgmActivePercent = null;
        }
        if (Hidden(LoopAnalysisSeries.LoopActivity))
        {
            range.Activity.ClosedLoop = null;
            range.Activity.Pump = null;
            range.Activity.AidActivePercent = null;
            range.Activity.PumpUsePercent = null;
        }
        if (Hidden(LoopAnalysisSeries.LoopGaps)) range.Cadence.LoopGaps = null;
        if (Hidden(LoopAnalysisSeries.UnrecordedDelivery)) range.Cadence.UnrecordedDelivery = null;
        if (Hidden(LoopAnalysisSeries.ZeroDelivery)) range.ZeroDelivery = null;
        if (Hidden(LoopAnalysisSeries.MaxBasal)) range.MaxBasal = null;
        if (Hidden(LoopAnalysisSeries.AtMaxIob)) range.AtMaxIob = null;
        if (Hidden(LoopAnalysisSeries.AutomaticBoluses)) range.AutomaticBoluses = null;
        if (Hidden(LoopAnalysisSeries.SuggestedNotEnacted)) range.SuggestedNotEnacted = null;
        if (Hidden(LoopAnalysisSeries.SensitivityRatio)) range.SensitivityRatio = null;
        if (Hidden(LoopAnalysisSeries.LoopLimits)) range.Limits.LoopMaximumBasalRate = null;
        if (Hidden(LoopAnalysisSeries.OrefMaxIob)) range.Limits.OrefMaxIob = null;

        var unknown = range.Unknown;
        if (Hidden(LoopAnalysisSeries.LoopGaps)) unknown.LoopGaps = null;
        if (Hidden(LoopAnalysisSeries.UnrecordedDelivery)) unknown.UnrecordedDelivery = null;
        if (Hidden(LoopAnalysisSeries.MaxBasal)) unknown.MaxBasal = null;
        if (Hidden(LoopAnalysisSeries.AtMaxIob)) unknown.AtMaxIob = null;
        if (Hidden(LoopAnalysisSeries.SuggestedNotEnacted)) unknown.SuggestedNotEnacted = null;
        if (Hidden(LoopAnalysisSeries.OrefMaxIob)) unknown.OrefMaxIob = null;
        if (Hidden(LoopAnalysisSeries.LoopLimits)) unknown.LoopMaximumBasalRate = null;
    }

    private static void Narrow(DailyProfileRange range, HashSet<LoopAnalysisSeries> withheld)
    {
        bool Hidden(LoopAnalysisSeries series) => withheld.Contains(series);

        foreach (var slot in range.Slots)
        {
            if (Hidden(LoopAnalysisSeries.Glucose)) slot.Glucose = null;
            if (Hidden(LoopAnalysisSeries.ScheduledBasal)) slot.ScheduledBasal = null;
            if (Hidden(LoopAnalysisSeries.DeliveredBasal)) slot.DeliveredBasal = null;
            if (Hidden(LoopAnalysisSeries.NetLoopCorrection)) slot.NetLoopCorrection = null;
            if (Hidden(LoopAnalysisSeries.NetLoopCorrectionComponents))
            {
                slot.ManualTemp = null;
                slot.Suspend = null;
                slot.Inferred = null;
            }
            if (Hidden(LoopAnalysisSeries.Iob))
            {
                slot.Iob = null;
                slot.IobSource = null;
            }
            if (Hidden(LoopAnalysisSeries.Cob))
            {
                slot.Cob = null;
                slot.CobSource = null;
            }
            if (Hidden(LoopAnalysisSeries.ManualBoluses)) slot.ManualBoluses = null;
            if (Hidden(LoopAnalysisSeries.Carbs)) slot.Carbs = null;
            if (Hidden(LoopAnalysisSeries.Target))
            {
                slot.TargetLow = null;
                slot.TargetHigh = null;
            }
        }

        foreach (var day in range.Days)
        {
            if (Hidden(LoopAnalysisSeries.Glucose)) day.Coverage.Glucose = null;
            if (Hidden(LoopAnalysisSeries.ScheduledBasal)) day.Coverage.ScheduledBasal = null;
            if (Hidden(LoopAnalysisSeries.DeliveredBasal)) day.Coverage.DeliveredBasal = null;
            if (Hidden(LoopAnalysisSeries.NetLoopCorrection))
            {
                day.Coverage.NetLoopCorrection = null;
                day.NetLoopCorrection = null;
            }
            if (Hidden(LoopAnalysisSeries.Iob) || Hidden(LoopAnalysisSeries.Cob)) day.Coverage.IobCob = null;
            if (Hidden(LoopAnalysisSeries.InsulinSummary))
            {
                day.TotalInsulin = null;
                day.DeliveredBasal = null;
                day.AutomaticBolus = null;
                day.ManualBolus = null;
            }
        }

        if (Hidden(LoopAnalysisSeries.HourlyNetLoopCorrection)) range.HourlyNetLoopCorrection = [];
        if (Hidden(LoopAnalysisSeries.NetLoopCorrectionSummary))
        {
            range.Summary.NetLoopCorrectionPerDay = null;
            range.Summary.NetLoopCorrectionTddPercent = null;
            range.Summary.NetLoopCorrectionDays = 0;
        }
        if (Hidden(LoopAnalysisSeries.InsulinSummary)) range.Summary.Insulin = null;
        if (Hidden(LoopAnalysisSeries.DeliveredToScheduledRatio)) range.Summary.DeliveredToScheduledRatio = null;

        range.Summary.DaysWithData = range.Days.Count(day =>
            (day.HasGlucose && !Hidden(LoopAnalysisSeries.Glucose))
            || (day.HasTreatments && !Hidden(LoopAnalysisSeries.DeliveredBasal)));
    }
}
