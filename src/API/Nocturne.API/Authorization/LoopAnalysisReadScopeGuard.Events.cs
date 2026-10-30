using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.API.Authorization;

/// <summary>
/// The Meals and events page. An anchor is drawn from a category as a series is: meals, correction
/// boluses and the starts of exercise, overrides and temporary targets are treatments, sensor
/// starts are devices, and an unannounced rise is read off glucose and the carbs and loop
/// correction around it. Every event is also read off glucose, since one counts only with a start
/// glucose, so every anchor needs <c>glucose.read</c> as well: otherwise the counts would say where
/// CGM data existed. A caller may ask only for anchors it could read, since the curves and counts
/// aligned on an anchor would otherwise place each one in time; the controller drops the rest
/// before anything is computed (<see cref="NarrowEvents"/>). The isolation and corroboration options
/// read meals, boluses and delivery, so they need treatments too. A meal cohort's food filter reads
/// the food catalogue, so it needs <c>food.read</c>; without it the filter is dropped and reported,
/// as an option is, and the cohort counts every meal.
/// </summary>
internal static partial class LoopAnalysisReadScopeGuard
{
    private static readonly IReadOnlyDictionary<EventAnchorKind, string[]> AnchorScopes =
        new Dictionary<EventAnchorKind, string[]>
        {
            [EventAnchorKind.Meal] = [Scope.GlucoseRead, Scope.TreatmentsRead],
            [EventAnchorKind.CorrectionBolus] = [Scope.GlucoseRead, Scope.TreatmentsRead],
            [EventAnchorKind.SensorStart] = [Scope.GlucoseRead, Scope.DevicesRead],
            [EventAnchorKind.ExerciseStart] = [Scope.GlucoseRead, Scope.TreatmentsRead],
            [EventAnchorKind.OverrideStart] = [Scope.GlucoseRead, Scope.TreatmentsRead],
            [EventAnchorKind.TemporaryTargetStart] = [Scope.GlucoseRead, Scope.TreatmentsRead],
            [EventAnchorKind.UnannouncedRise] = [Scope.GlucoseRead, Scope.TreatmentsRead],
        };

    /// <summary>The options beside the cohorts, by query parameter, and how to drop each.</summary>
    private static readonly (string Name, Func<EventResponseDefinition, bool> IsSet, Action<EventResponseDefinition> Clear)[] EventOptions =
    [
        ("isolatedOnly", d => d.IsolatedOnly, d => d.IsolatedOnly = false),
        ("requireCorroboration", d => d.RequireCorroboration, d => d.RequireCorroboration = false),
    ];

    /// <summary>The cohort food filters, by the query parameter each is sent as, and how to drop each.</summary>
    private static readonly (string Name, Func<EventCohortDefinition, bool> IsSet, Action<EventCohortDefinition> Clear)[] FoodFilters =
    [
        ("foodIds", c => c.FoodIds is { Count: > 0 }, c => c.FoodIds = null),
        ("foodCategory", c => !string.IsNullOrWhiteSpace(c.FoodCategory), c => c.FoodCategory = null),
    ];

    /// <summary>The first scope an anchor kind needs that the caller lacks, or null.</summary>
    private static string? MissingScope(EventAnchorKind anchor, IReadOnlySet<string> grantedScopes) =>
        AnchorScopes[anchor].FirstOrDefault(scope => !AnalyticsReadScopes.Allows(grantedScopes, scope));

    /// <summary>
    /// Drops from <paramref name="definition"/> every cohort whose anchor the caller could not read
    /// and the options without <c>treatments.read</c>. Without <c>food.read</c> a food-filtered
    /// cohort loses its filter and is marked <see cref="EventCohortDefinition.FoodFilterWithheld"/>,
    /// so it counts no events rather than every meal.
    /// </summary>
    /// <returns>
    /// The query parameters ignored: each dropped cohort's <c>anchor{n}</c>, the options, and each
    /// dropped food filter's <c>foodIds{n}</c> or <c>foodCategory{n}</c>.
    /// </returns>
    public static List<string> NarrowEvents(EventResponseDefinition definition, IReadOnlySet<string> grantedScopes)
    {
        var ignored = definition.Cohorts
            .Where(c => MissingScope(c.Anchor, grantedScopes) is not null)
            .Select(c => $"anchor{c.Cohort}")
            .ToList();
        definition.Cohorts.RemoveAll(c => MissingScope(c.Anchor, grantedScopes) is not null);

        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.TreatmentsRead))
        {
            foreach (var option in EventOptions.Where(o => o.IsSet(definition)))
            {
                option.Clear(definition);
                ignored.Add(option.Name);
            }
        }

        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.FoodRead))
        {
            foreach (var cohort in definition.Cohorts)
            {
                foreach (var filter in FoodFilters.Where(f => f.IsSet(cohort)))
                {
                    filter.Clear(cohort);
                    cohort.FoodFilterWithheld = true;
                    ignored.Add($"{filter.Name}{cohort.Cohort}");
                }
            }
        }
        return ignored;
    }

    public static List<EventUnavailableAnchor> UnavailableAnchors(IReadOnlySet<string> grantedScopes) =>
    [
        .. AnchorScopes.Keys
            .Select(anchor => (anchor, scope: MissingScope(anchor, grantedScopes)))
            .Where(a => a.scope is not null)
            .Select(a => new EventUnavailableAnchor { Anchor = a.anchor, RequiredScope = a.scope! }),
    ];

    /// <summary>The event options <see cref="NarrowEvents"/> would drop for this caller, set or not.</summary>
    /// <remarks>The food filters are named without their cohort number.</remarks>
    public static List<LoopAnalysisUnavailableFilter> UnavailableEventFilters(IReadOnlySet<string> grantedScopes) =>
    [
        .. AnalyticsReadScopes.Allows(grantedScopes, Scope.TreatmentsRead)
            ? []
            : EventOptions.Select(o => new LoopAnalysisUnavailableFilter { Filter = o.Name, RequiredScope = Scope.TreatmentsRead }),
        .. AnalyticsReadScopes.Allows(grantedScopes, Scope.FoodRead)
            ? []
            : FoodFilters.Select(f => new LoopAnalysisUnavailableFilter { Filter = f.Name, RequiredScope = Scope.FoodRead }),
    ];

    /// <summary>The event series and figures the caller's scopes withhold, as <see cref="WithheldDailyProfile"/> is for the Daily profile.</summary>
    public static HashSet<LoopAnalysisSeries> WithheldEvents(IReadOnlySet<string> grantedScopes)
    {
        var withheld = new HashSet<LoopAnalysisSeries>();
        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.GlucoseRead))
            withheld.Add(LoopAnalysisSeries.EventGlucose);
        if (!AnalyticsReadScopes.Allows(grantedScopes, Scope.TreatmentsRead))
            withheld.UnionWith([
                LoopAnalysisSeries.EventNetInsulin, LoopAnalysisSeries.EventIob,
                LoopAnalysisSeries.EventCob, LoopAnalysisSeries.EventList,
            ]);
        return withheld;
    }

    /// <summary>
    /// Without glucose there is no event at all, since each counts only with a start glucose; so the
    /// cohorts and the list go with it, counts included.
    /// </summary>
    private static void Narrow(EventResponseRange range, HashSet<LoopAnalysisSeries> withheld)
    {
        bool Hidden(LoopAnalysisSeries series) => withheld.Contains(series);

        if (Hidden(LoopAnalysisSeries.EventGlucose))
        {
            range.Cohorts = [];
            range.Events = [];
            return;
        }
        if (Hidden(LoopAnalysisSeries.EventList))
            range.Events = [];
        foreach (var e in range.Events)
            ClearMetrics(e.Metrics, Hidden);

        foreach (var cohort in range.Cohorts)
        {
            foreach (var slot in cohort.Slots)
            {
                if (Hidden(LoopAnalysisSeries.EventNetInsulin))
                {
                    slot.NetLoopCorrection = null;
                    slot.NetInsulinSinceAnchor = null;
                    slot.NetLoopCorrectionSinceAnchor = null;
                }
                if (Hidden(LoopAnalysisSeries.EventIob)) slot.Iob = null;
                if (Hidden(LoopAnalysisSeries.EventCob)) slot.Cob = null;
            }
            if (Hidden(LoopAnalysisSeries.EventNetInsulin))
            {
                cohort.GramsPerUnitNotIsolated = 0;
                cohort.GramsPerUnitNoNetInsulin = 0;
                cohort.GramsPerUnitUncovered = 0;
            }
            ClearMetrics(cohort.Metrics, Hidden);
        }
    }

    /// <summary>
    /// Each figure of <see cref="EventMetrics"/> by the series it belongs to; the same names on
    /// <see cref="EventMetricsSummary"/> carry its spread. Glucose figures need no entry: without
    /// glucose there are no events.
    /// </summary>
    private static readonly (LoopAnalysisSeries Series, string[] Metrics)[] MetricSeries =
    [
        (LoopAnalysisSeries.EventNetInsulin,
        [
            nameof(EventMetrics.TotalInsulin), nameof(EventMetrics.NetInsulin), nameof(EventMetrics.GramsPerUnit),
            nameof(EventMetrics.DropPerUnit),
            nameof(EventMetrics.NetInsulinFirst30), nameof(EventMetrics.NetInsulinFirst60), nameof(EventMetrics.NetInsulinFirst120),
        ]),
        (LoopAnalysisSeries.EventIob, [nameof(EventMetrics.IobAtAnchor), nameof(EventMetrics.IobAtEnd)]),
    ];

    /// <param name="metrics">An <see cref="EventMetrics"/> or an <see cref="EventMetricsSummary"/>.</param>
    /// <param name="hidden">Whether a series is withheld.</param>
    private static void ClearMetrics(object metrics, Func<LoopAnalysisSeries, bool> hidden)
    {
        foreach (var (_, names) in MetricSeries.Where(m => hidden(m.Series)))
            foreach (var name in names)
                metrics.GetType().GetProperty(name)!.SetValue(metrics, null);
    }
}
