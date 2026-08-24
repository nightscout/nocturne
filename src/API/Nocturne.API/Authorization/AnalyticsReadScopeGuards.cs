using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
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
            data.MaxIob = 0;
            data.MaxCob = 0;
        }

        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.DevicesRead))
        {
            data.DeviceEventMarkers = [];
            data.SystemEventMarkers = [];
            data.PumpModeSpans = [];
        }

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
/// Trackers carry no <see cref="ShareDataCategories"/> category and are gated by the fallback
/// policy wherever else they are served, so they are not redacted here.
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
/// reservoir and battery readings are the device category, while the sensitivity adjustment is read
/// off the active therapy profile.
/// </summary>
/// <seealso cref="AnalyticsReadScopes"/>
internal static class CurrentTherapyStateReadScopeGuard
{
    public static readonly IReadOnlyList<string> AdmissionScopes =
    [
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

        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.TherapyRead))
            data.SensitivityPercent = null;

        return data;
    }
}
