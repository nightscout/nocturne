using System.Globalization;
using System.Text;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;
using static Nocturne.API.Mcp.Reports.Datasets.LoopAnalysisDatasetRun;

namespace Nocturne.API.Mcp.Reports.Datasets;

/// <summary>
/// The Meals &amp; events page (<c>GET /api/v4/loop-analysis/event-response</c>) as a dataset: the
/// cohorts become the endpoint's <see cref="EventResponseQuery"/>, so the definition, its narrowing
/// for the caller and the calculation are the endpoint's own. Drawn as <c>range_bands</c> its rows
/// are each cohort's five-minute slots from the event; drawn as <c>table</c>, each event.
/// </summary>
internal sealed class EventResponseDataset : IReportDataset
{
    /// <summary>The most events a table carries; the summary counts every one.</summary>
    public const int MaxEvents = 2000;

    /// <summary>The most single events a glucose chart draws per cohort, newest first; the band covers every one.</summary>
    public const int MaxTracesPerCohort = 30;

    /// <summary>The most points one event's trace carries; a longer window is thinned to every n-th slot.</summary>
    public const int TracePointBudget = 72;

    public string Name => EventResponseParameters.Name;

    public string Description =>
        "Meals & events: up to three cohorts of events (meals, correction boluses, sensor starts, exercise, override and "
        + "temporary target starts, unannounced rises), each narrowed by time of day, weekday and, for meals, by size and by "
        + "the foods a meal carries a food record of (ids from find_foods). Drawn as range_bands: one row per cohort and "
        + "five-minute slot from the event, with glucose and glucose change percentiles (P10, P25, P50, P75, P90) across "
        + "the cohort's events and the median IOB, COB and net loop correction. Drawn as table: one row per event with its "
        + "figures and a one-sentence summary. Drawn as glucose_chart: each cohort's median and middle half over the "
        + $"window, with up to {MaxTracesPerCohort} single events per cohort (the newest) drawn faintly under it. Reads local days; needs glucose.read, and treatments.read for meal, bolus, "
        + "exercise, override, temporary target and rise cohorts, devices.read for sensor starts, food.read for a food filter.";

    public Type ParametersType => typeof(EventResponseParameters);

    public string RequiredScope => Scope.GlucoseRead;

    private static readonly IReadOnlyList<ReportField> SlotFields =
    [
        new("series", "Series", ReportFieldKind.Category, null, "The cohort and range the row belongs to, as a label."),
        new("cohort", "Cohort", ReportFieldKind.Number, null, "The cohort's number, 1 to 3, in the order the cohorts were sent.", Companion: true),
        new("range", "Range", ReportFieldKind.Category, null, "primary, or comparison for the comparison range.", Companion: true),
        new("minute", "Minutes from event", ReportFieldKind.Offset, "min", "Start of the five-minute slot, minutes from the event; negative before it."),
        new("glucoseP10", "Glucose P10", ReportFieldKind.Number, ReportField.GlucoseUnit, "10th percentile of glucose across the cohort's events in the slot."),
        new("glucoseP25", "Glucose P25", ReportFieldKind.Number, ReportField.GlucoseUnit, "25th percentile of glucose."),
        new("glucoseP50", "Glucose median", ReportFieldKind.Number, ReportField.GlucoseUnit, "Median glucose.", GlucoseScale: true),
        new("glucoseP75", "Glucose P75", ReportFieldKind.Number, ReportField.GlucoseUnit, "75th percentile of glucose."),
        new("glucoseP90", "Glucose P90", ReportFieldKind.Number, ReportField.GlucoseUnit, "90th percentile of glucose."),
        new("deltaP10", "Change P10", ReportFieldKind.Number, ReportField.GlucoseUnit, "10th percentile of glucose minus the glucose at the event."),
        new("deltaP25", "Change P25", ReportFieldKind.Number, ReportField.GlucoseUnit, "25th percentile of the change from the event."),
        new("deltaP50", "Change median", ReportFieldKind.Number, ReportField.GlucoseUnit, "Median change from the event."),
        new("deltaP75", "Change P75", ReportFieldKind.Number, ReportField.GlucoseUnit, "75th percentile of the change from the event."),
        new("deltaP90", "Change P90", ReportFieldKind.Number, ReportField.GlucoseUnit, "90th percentile of the change from the event."),
        new("iobP50", "IOB median", ReportFieldKind.Number, "U", "Median insulin on board.", Scope.TreatmentsRead, ChartColor.InsulinBolus),
        new("cobP50", "COB median", ReportFieldKind.Number, "g", "Median carbs on board.", Scope.TreatmentsRead, ChartColor.Carbs),
        new("netLoopCorrectionP50", "Net loop correction median", ReportFieldKind.Number, "U/h",
            "Median of the algorithm's temp basal above schedule plus its automatic boluses.", Scope.TreatmentsRead, ChartColor.InsulinBasal),
        new("events", "Events", ReportFieldKind.Number, null, "Events with a glucose value in the slot."),
        new(ReportField.SentenceField, "Sentence", ReportFieldKind.Category, null,
            "One sentence that states the slot's glucose figures for its cohort; the view hands it to the assistant when the slot is picked.", Companion: true),
    ];

    private static readonly IReadOnlyList<ReportField> EventFields =
    [
        new("at", "Time", ReportFieldKind.Time, null, "When the event was, Unix milliseconds."),
        new("anchor", "Event", ReportFieldKind.Category, null, "The event kind: Meal, CorrectionBolus, SensorStart, and so on."),
        new("grams", "Carbs", ReportFieldKind.Number, "g", "A meal's carbs."),
        new("cohorts", "Cohorts", ReportFieldKind.Category, null, "The cohorts that counted the event, by number."),
        new("startGlucose", "Start glucose", ReportFieldKind.Number, ReportField.GlucoseUnit, "The reading nearest the event, within 5 minutes."),
        new("peakRise", "Peak rise", ReportFieldKind.Number, ReportField.GlucoseUnit, "Rise events: the highest reading after the event minus the start."),
        new("minutesToPeak", "Time to peak", ReportFieldKind.Number, "min", "Rise events: minutes from the event to the peak."),
        new("minutesBackToStart", "Back to start", ReportFieldKind.Number, "min",
            "Rise events: minutes until glucose came back near the start; empty when it had not by the window's end."),
        new("drop", "Drop", ReportFieldKind.Number, ReportField.GlucoseUnit, "Fall events: the start minus the reading at the window's end."),
        new("nadir", "Lowest", ReportFieldKind.Number, ReportField.GlucoseUnit, "Fall events: the lowest reading after the event."),
        new("minutesToNadir", "Time to lowest", ReportFieldKind.Number, "min", "Fall events: minutes from the event to the lowest reading."),
        new("netInsulin", "Net insulin", ReportFieldKind.Number, "U", "Insulin above the basal schedule over the window.", Scope.TreatmentsRead),
        new("gramsPerUnit", "Carbs per unit", ReportFieldKind.Number, "g/U",
            "An isolated meal's carbs over its net insulin.", Scope.TreatmentsRead),
        new("summary", "Summary", ReportFieldKind.Category, null, "One sentence that states what the event's figures show."),
    ];

    public IReadOnlyList<ReportField> Fields { get; } = [.. SlotFields, .. EventFields];

    public IReadOnlyList<ReportField> FieldsFor(ReportPrimitive primitive) =>
        primitive == ReportPrimitive.Table ? EventFields : SlotFields;

    public ReportBindings? DefaultBindings(ReportPrimitive primitive) => primitive switch
    {
        ReportPrimitive.RangeBands => new ReportBindings
        {
            X = "minute",
            Y = ["glucoseP50"],
            Band = ["glucoseP10", "glucoseP25", "glucoseP75", "glucoseP90"],
            Series = "series",
        },
        ReportPrimitive.GlucoseChart => new ReportBindings
        {
            X = "minute",
            Y = ["glucoseP50"],
            Band = ["glucoseP25", "glucoseP75"],
            Series = "series",
        },
        _ => null,
    };

    public IReadOnlyList<ReportPrimitive> AllowedPrimitives { get; } =
        [ReportPrimitive.RangeBands, ReportPrimitive.Table, ReportPrimitive.GlucoseChart];

    public TimeSpan DefaultRange => TimeSpan.FromDays(30);

    public TimeSpan MaxRange(ReportPrimitive primitive) => LoopAnalysisDatasetRun.MaxRange;

    public int PointBudget => MaxEvents;

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> SampleRows { get; } =
    [
        new Dictionary<string, object?>
        {
            ["series"] = "Meal: all", ["cohort"] = 1, ["range"] = "primary", ["minute"] = 60,
            ["glucoseP10"] = 120.0, ["glucoseP25"] = 140.0, ["glucoseP50"] = 165.0, ["glucoseP75"] = 190.0, ["glucoseP90"] = 215.0,
            ["deltaP10"] = 10.0, ["deltaP25"] = 30.0, ["deltaP50"] = 55.0, ["deltaP75"] = 80.0, ["deltaP90"] = 100.0,
            ["iobP50"] = 2.4, ["cobP50"] = 22.0, ["netLoopCorrectionP50"] = 0.6, ["events"] = 14,
            [ReportField.SentenceField] = "Meal: all, 60 minutes after the meal: median 165 mg/dL, middle half 140 to 190 mg/dL, 14 events.",
        },
        new Dictionary<string, object?>
        {
            ["at"] = 1793260800000L, ["anchor"] = "Meal", ["grams"] = 45.0, ["cohorts"] = "1", ["startGlucose"] = 110.0,
            ["peakRise"] = 70.0, ["minutesToPeak"] = 55.0, ["minutesBackToStart"] = 175.0, ["netInsulin"] = 4.1,
            ["gramsPerUnit"] = 11.0,
            ["summary"] = "Meal of 45 g on 2026-10-29 at 08:00: started at 110 mg/dL, rose 70 mg/dL to its peak 55 minutes later, and was back near the start after 175 minutes. Insulin above the basal schedule: 4.1 U.",
        },
    ];

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters)
    {
        var p = (EventResponseParameters)parameters;
        if (p.Cohorts is { Count: 0 or > EventResponseDefinition.MaxCohorts })
            yield return ("cohorts", $"Send 1 to {EventResponseDefinition.MaxCohorts} cohorts, or leave 'cohorts' out for every meal.");
        if (!EventResponseDefinition.MinutesBeforeChoices.Contains(p.MinutesBefore))
            yield return ("minutesBefore", $"Send one of: {string.Join(", ", EventResponseDefinition.MinutesBeforeChoices)}.");
        if (!EventResponseDefinition.MinutesAfterChoices.Contains(p.MinutesAfter))
            yield return ("minutesAfter", $"Send one of: {string.Join(", ", EventResponseDefinition.MinutesAfterChoices)}.");
        if (p.ProfileName is { Length: > LoopAnalysisDatasetParameters.MaxProfileNameLength })
            yield return ("profileName", $"Use {LoopAnalysisDatasetParameters.MaxProfileNameLength} characters or fewer.");
        if (p.ComparisonRange is { } comparison)
        {
            if (comparison.To < comparison.From)
                yield return ("comparisonRange.to", "'to' must not be before 'from'.");
            if (comparison.From.Year < 2000 || comparison.To.Year > 2999)
                yield return ("comparisonRange", "Send dates from 2000 on.");
        }

        var cohorts = p.Cohorts ?? [];
        for (var i = 0; i < cohorts.Count && i < EventResponseDefinition.MaxCohorts; i++)
        {
            var c = cohorts[i];
            var at = $"cohorts[{i}]";
            var meal = c.Anchor == EventAnchorKind.Meal;
            if (!Enum.IsDefined(c.Anchor))
                yield return ($"{at}.anchor", "Send one of: Meal, CorrectionBolus, SensorStart, ExerciseStart, OverrideStart, TemporaryTargetStart, UnannouncedRise.");
            if (c.TimeOfDay is { } window && (window.FromMinute is < 0 or > 1439 || window.ToMinute is < 0 or > 1439))
                yield return ($"{at}.timeOfDay", "Send minutes after midnight, 0 to 1439.");
            if (c.Grams is { } grams)
            {
                if (!meal)
                    yield return ($"{at}.grams", "Only a Meal cohort takes grams. Remove it.");
                else if (grams.Min is < 0 or > EventResponseDefinition.MaxGrams || grams.Max is < 0 or > EventResponseDefinition.MaxGrams)
                    yield return ($"{at}.grams", $"Send grams from 0 to {EventResponseDefinition.MaxGrams}.");
            }
            if (c.HasFoodFilter && !meal)
                yield return ($"{at}.foods", "Only a Meal cohort takes foods or a food category. Remove them.");
            if (c.Foods is { Count: 0 or > EventResponseDefinition.MaxFoods })
                yield return ($"{at}.foods", $"Send 1 to {EventResponseDefinition.MaxFoods} food ids, or leave 'foods' out.");
            if (c.FoodCategory is { Length: > EventResponseDefinition.MaxFoodCategoryLength })
                yield return ($"{at}.foodCategory", $"Use {EventResponseDefinition.MaxFoodCategoryLength} characters or fewer.");
            if (c.DayOfWeek is { Count: 0 })
                yield return ($"{at}.dayOfWeek", "Send at least one weekday, or leave 'dayOfWeek' out for every day.");
            if (c.DayOfWeek is { } days && days.Any(d => !Enum.IsDefined(d)))
                yield return ($"{at}.dayOfWeek", "Send weekdays as Mon, Tue, Wed, Thu, Fri, Sat or Sun.");
        }
    }

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var parameters = (EventResponseParameters)request.Parameters;
        var services = request.Services;
        var useMmol = request.UseMmol;

        // A food filter the token cannot read would otherwise widen the cohort to every meal; the cohort is left out instead.
        var foodRead = request.HasScope(Scope.FoodRead);
        var asked = parameters.AppliedCohorts;
        var kept = new List<(ReportEventCohort Cohort, int Number)>();
        var notes = new List<string>();
        for (var i = 0; i < asked.Count; i++)
        {
            if (asked[i].HasFoodFilter && !foodRead)
                notes.Add($"Cohort {i + 1} is not shown: its food filter needs the food.read scope, which this token does not hold.");
            else
                kept.Add((asked[i], i + 1));
        }

        // With every cohort left out there is nothing to read; an empty query would mean every meal.
        var definition = kept.Count > 0
            ? parameters.ToEventQuery([.. kept.Select(k => k.Cohort)]).ToDefinition()
            : new EventResponseDefinition
            {
                MinutesBefore = parameters.MinutesBefore,
                MinutesAfter = parameters.MinutesAfter,
                IsolatedOnly = parameters.IsolatedOnly,
                RequireCorroboration = parameters.RequireCorroboration,
            };
        var number = definition.Cohorts.ToDictionary(c => c.Cohort, c => kept[c.Cohort - 1].Number);
        var ignored = LoopAnalysisReadScopeGuard.NarrowEvents(definition, request.GrantedScopes)
            .Select(name => name.StartsWith("anchor", StringComparison.Ordinal) && int.TryParse(name[6..], out var n) && number.TryGetValue(n, out var original)
                ? $"cohorts[{original - 1}] ({asked[original - 1].Anchor})"
                : name)
            .ToList();

        var resolved = await ResolveAsync(request, parameters, parameters.ComparisonRange, cancellationToken);
        var report = resolved.Filter is { } filter && resolved.Zone is { } zone && definition.Cohorts.Count > 0
            ? await services.GetRequiredService<ILoopAnalysisService>().GetEventResponseAsync(filter, definition, zone, cancellationToken)
            : new EventResponseReport();
        report.Definition = definition;
        report = Serve(resolved, report, request);
        report.IgnoredFilters.AddRange(ignored);

        var askedDefinition = parameters.ToEventQuery(asked).ToDefinition();
        var foodNames = await FoodNamesAsync(services, askedDefinition, foodRead, cancellationToken);
        var askedLabels = askedDefinition.Cohorts.Select(c => CohortLabel(c, foodNames, foodRead)).ToList();
        var labels = definition.Cohorts.ToDictionary(c => c.Cohort, c => askedLabels[number[c.Cohort] - 1]);
        var slotMinutes = EventResponseCalculator.SlotMinutes(definition);

        var events = report.Primary.Events;
        RecordReads(request.ReadLog, report, ran: definition.Cohorts.Count > 0 && resolved.Filter is not null && resolved.Zone is not null,
            readFoodNames: foodNames.Count > 0);

        var rows = request.Primitive == ReportPrimitive.Table
            ? EventRows(events.Take(MaxEvents), labels, number, useMmol)
            : SlotRows(report, slotMinutes, labels, number, useMmol);

        var fieldSeries = new Dictionary<string, LoopAnalysisSeries>
        {
            ["iobP50"] = LoopAnalysisSeries.EventIob,
            ["cobP50"] = LoopAnalysisSeries.EventCob,
            ["netLoopCorrectionP50"] = LoopAnalysisSeries.EventNetInsulin,
            ["netInsulin"] = LoopAnalysisSeries.EventNetInsulin,
            ["gramsPerUnit"] = LoopAnalysisSeries.EventNetInsulin,
        };
        if (request.Primitive == ReportPrimitive.Table)
            foreach (var field in EventFields)
                fieldSeries.TryAdd(field.Name, LoopAnalysisSeries.EventList);

        if (request.Primitive == ReportPrimitive.Table && events.Count > MaxEvents)
            notes.Add($"The table shows the first {MaxEvents} of {events.Count} events.");

        IReadOnlyList<ReportTrace> traces = [];
        if (request.Primitive == ReportPrimitive.GlucoseChart)
        {
            var (drawn, note) = Traces(report, slotMinutes, number, useMmol);
            traces = drawn;
            if (note is not null)
                notes.Add(note);
        }

        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = Summarise(report, labels, number, notes, useMmol),
            Facts = FactsOf(report, labels, useMmol),
            Sentence = NoTimeZone(report, "Meals & events") ?? WindowSentence(report.Definition),
            Context = ContextLine(report, ReportFacts.Plural(report.Primary.Events.Count, "event")),
            Omitted = Omitted(report, fieldSeries),
            Window = Window(report),
            Cohorts = [.. askedLabels.Select((label, i) => new ReportCohort(i + 1, label))],
            Traces = traces,
        };
    }

    /// <summary>
    /// Each cohort's newest events as traces of their own glucose, at most
    /// <see cref="MaxTracesPerCohort"/> a cohort and <see cref="TracePointBudget"/> points a trace,
    /// and the sentence that says how many were drawn and whether the budget thinned them.
    /// </summary>
    internal static (IReadOnlyList<ReportTrace> Traces, string? Note) Traces(
        EventResponseReport report, IReadOnlyList<int> slotMinutes, IReadOnlyDictionary<int, int> number, bool useMmol)
    {
        var step = Math.Max(1, (int)Math.Ceiling(slotMinutes.Count / (double)TracePointBudget));
        var traces = new List<ReportTrace>();
        var counts = new List<string>();
        foreach (var cohort in report.Definition.Cohorts)
        {
            var events = report.Primary.Events.Where(e => e.Cohorts.Contains(cohort.Cohort)).OrderByDescending(e => e.At).ToList();
            var drawn = events.Take(MaxTracesPerCohort).ToList();
            var shown = number.GetValueOrDefault(cohort.Cohort, cohort.Cohort);
            foreach (var e in drawn)
            {
                var points = new List<double[]>();
                for (var k = 0; k < e.GlucoseTrace.Count && k < slotMinutes.Count; k += step)
                    if (e.GlucoseTrace[k] is { } mgdl)
                        points.Add([slotMinutes[k], Glucose(mgdl, useMmol)!.Value]);
                traces.Add(new ReportTrace(shown, new DateTimeOffset(DateTime.SpecifyKind(e.At, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                    points, EventSentence(e, useMmol)));
            }
            if (events.Count > 0)
                counts.Add(drawn.Count == events.Count
                    ? $"cohort {shown}, all {events.Count}"
                    : $"cohort {shown}, the newest {drawn.Count} of {events.Count}");
        }
        if (counts.Count == 0)
            return (traces, null);
        var note = $"Single events drawn under the band: {string.Join("; ", counts)}.";
        if (step > 1)
            note += $" Each event's line has one point every {step * EventResponseDefinition.SlotMinutes} minutes, to stay within {TracePointBudget} points.";
        return (traces, note);
    }

    /// <summary>The category each anchor's events are records of, as the read audit names it.</summary>
    internal static string AnchorCategory(EventAnchorKind anchor) =>
        anchor == EventAnchorKind.SensorStart ? "device_events" : "treatments";

    /// <summary>
    /// The read-audit categories of what the panel read: glucose (readings, not counted here), each
    /// cohort anchor's category with the events it read, treatments for the insulin and carb series
    /// where they were served, and foods where a food filter ran or names were read.
    /// </summary>
    internal static void RecordReads(McpToolReadLog log, EventResponseReport report, bool ran, bool readFoodNames)
    {
        if (readFoodNames)
            log.Record("foods", 0);
        if (!ran)
            return;
        log.Record("glucose", 0);
        var events = report.Primary.Events.Concat(report.Comparison?.Events ?? []).ToList();
        foreach (var anchor in report.Definition.Cohorts.Select(c => c.Anchor).Distinct())
            log.Record(AnchorCategory(anchor), events.Count(e => e.Anchor == anchor));
        if (events.Count > 0 && new[] { LoopAnalysisSeries.EventIob, LoopAnalysisSeries.EventCob, LoopAnalysisSeries.EventNetInsulin }
                .Any(s => !report.WithheldSeries.Contains(s)))
            log.Record("treatments", 0);
        if (report.Definition.Cohorts.Any(c => c.HasFoodFilter))
            log.Record("foods", 0);
    }

    /// <summary>The names of the foods the cohorts filter on, by id, in one read, where the token reads foods.</summary>
    private static async Task<IReadOnlyDictionary<Guid, string>> FoodNamesAsync(
        IServiceProvider services, EventResponseDefinition definition, bool foodRead, CancellationToken ct)
    {
        var ids = definition.Cohorts.SelectMany(c => c.FoodIds ?? []).Distinct().ToList();
        if (!foodRead || ids.Count == 0)
            return new Dictionary<Guid, string>();
        var foods = await services.GetRequiredService<IFoodRepository>().GetFoodsByIdsAsync(ids, ct);
        return foods.ToDictionary(f => f.Key, f => f.Value.Name);
    }

    /// <summary>
    /// A cohort's kind and the filter that sets it apart: "Meal: Penne or Rice, 06:00-10:00, Mon Tue",
    /// or "Meal: all" with no filter, so two cohorts of one kind never read the same.
    /// </summary>
    /// <param name="foodRead">The token reads foods; without it a food filter is named, not its foods.</param>
    internal static string CohortLabel(EventCohortDefinition cohort, IReadOnlyDictionary<Guid, string> foodNames, bool foodRead = true)
    {
        var parts = new List<string>();
        if (cohort.HasFoodFilter && !foodRead)
            parts.Add("a food filter");
        else
        {
            if (cohort.FoodIds is { Count: > 0 } ids)
                parts.Add(string.Join(" or ", ids.Select(id => foodNames.GetValueOrDefault(id, id.ToString()))));
            if (cohort.FoodCategory is { } category && !string.IsNullOrWhiteSpace(category))
                parts.Add($"category {category.Trim()}");
        }
        if (cohort.FromMinute is not null || cohort.ToMinute is not null)
            parts.Add($"{Clock(cohort.FromMinute ?? 0)}-{Clock(cohort.ToMinute ?? 0)}");
        if (cohort.MinGrams is not null || cohort.MaxGrams is not null)
            parts.Add(cohort.MinGrams is { } min && cohort.MaxGrams is { } max ? $"{Number(min)}-{Number(max)} g"
                : cohort.MinGrams is { } atLeast ? $"at least {Number(atLeast)} g" : $"at most {Number(cohort.MaxGrams!.Value)} g");
        if (cohort.DaysOfWeek is { Count: > 0 } days)
            parts.Add(string.Join(' ', days.Select(d => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(d))));
        return $"{AnchorWords(cohort.Anchor)}: {(parts.Count == 0 ? "all" : string.Join(", ", parts))}";
    }

    /// <summary>The anchor as the start of a sentence or label: "Meal", "Correction bolus".</summary>
    internal static string AnchorWords(EventAnchorKind anchor) => anchor switch
    {
        EventAnchorKind.Meal => "Meal",
        EventAnchorKind.CorrectionBolus => "Correction bolus",
        EventAnchorKind.SensorStart => "Sensor start",
        EventAnchorKind.ExerciseStart => "Exercise start",
        EventAnchorKind.OverrideStart => "Override start",
        EventAnchorKind.TemporaryTargetStart => "Temporary target start",
        EventAnchorKind.UnannouncedRise => "Unannounced rise",
        _ => anchor.ToString(),
    };

    /// <summary>
    /// The sentence for one cohort's slot: where it is from the event, and its glucose median, middle
    /// half and count of events.
    /// </summary>
    internal static string SlotSentence(string label, EventAnchorKind anchor, int minute, bool comparison, LoopAnalysisAggregate? glucose, bool useMmol)
    {
        var noun = AnchorWords(anchor).ToLowerInvariant();
        var when = minute switch
        {
            0 => $"at the {noun}",
            > 0 => $"{minute} minutes after the {noun}",
            _ => $"{-minute} minutes before the {noun}",
        };
        var who = comparison ? $"{label} (comparison range)" : label;
        return $"{who}, {when}: {GlucoseSpread(glucose, glucose?.N ?? 0, "event", useMmol)}";
    }

    /// <summary>"median 175 mg/dL, middle half 140 to 210 mg/dL, 32 events.", or "no readings." for an empty slot.</summary>
    internal static string GlucoseSpread(LoopAnalysisAggregate? glucose, int n, string noun, bool useMmol)
    {
        if (glucose is null || n == 0)
            return "no readings.";
        var unit = useMmol ? "mmol/L" : "mg/dL";
        string G(double mgdl) => Number(Glucose(mgdl, useMmol)!.Value, useMmol ? "0.0" : "0");
        return $"median {G(glucose.P50)} {unit}, middle half {G(glucose.P25)} to {G(glucose.P75)} {unit}, {n} {(n == 1 ? noun : noun + "s")}.";
    }

    private static List<IReadOnlyDictionary<string, object?>> SlotRows(
        EventResponseReport report,
        IReadOnlyList<int> slotMinutes,
        IReadOnlyDictionary<int, string> labels,
        IReadOnlyDictionary<int, int> number,
        bool useMmol)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        void Add(EventResponseRange? range, string name)
        {
            if (range is null)
                return;
            foreach (var cohort in range.Cohorts)
            {
                if (!labels.TryGetValue(cohort.Cohort, out var label))
                    continue;
                var series = name == "primary" ? label : label + " (comparison)";
                var anchor = report.Definition.Cohorts.FirstOrDefault(c => c.Cohort == cohort.Cohort)?.Anchor ?? EventAnchorKind.Meal;
                for (var k = 0; k < cohort.Slots.Count && k < slotMinutes.Count; k++)
                {
                    var slot = cohort.Slots[k];
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["series"] = series,
                        ["cohort"] = number[cohort.Cohort],
                        ["range"] = name,
                        ["minute"] = slotMinutes[k],
                        ["glucoseP10"] = Glucose(slot.Glucose?.P10, useMmol),
                        ["glucoseP25"] = Glucose(slot.Glucose?.P25, useMmol),
                        ["glucoseP50"] = Glucose(slot.Glucose?.P50, useMmol),
                        ["glucoseP75"] = Glucose(slot.Glucose?.P75, useMmol),
                        ["glucoseP90"] = Glucose(slot.Glucose?.P90, useMmol),
                        ["deltaP10"] = Glucose(slot.GlucoseChange?.P10, useMmol),
                        ["deltaP25"] = Glucose(slot.GlucoseChange?.P25, useMmol),
                        ["deltaP50"] = Glucose(slot.GlucoseChange?.P50, useMmol),
                        ["deltaP75"] = Glucose(slot.GlucoseChange?.P75, useMmol),
                        ["deltaP90"] = Glucose(slot.GlucoseChange?.P90, useMmol),
                        ["iobP50"] = Round(slot.Iob?.P50),
                        ["cobP50"] = Round(slot.Cob?.P50, 1),
                        ["netLoopCorrectionP50"] = Round(slot.NetLoopCorrection?.P50, 3),
                        ["events"] = slot.Glucose?.N ?? 0,
                        [ReportField.SentenceField] = SlotSentence(label, anchor, slotMinutes[k], name == "comparison", slot.Glucose, useMmol),
                    });
                }
            }
        }
        Add(report.Primary, "primary");
        Add(report.Comparison, "comparison");
        return rows;
    }

    private static List<IReadOnlyDictionary<string, object?>> EventRows(
        IEnumerable<EventResponseEvent> events,
        IReadOnlyDictionary<int, string> labels,
        IReadOnlyDictionary<int, int> number,
        bool useMmol) =>
    [
        .. events.Select(e => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["at"] = new DateTimeOffset(DateTime.SpecifyKind(e.At, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            ["anchor"] = e.Anchor.ToString(),
            ["grams"] = e.Grams,
            ["cohorts"] = string.Join(", ", e.Cohorts.Select(c => number.GetValueOrDefault(c, c))),
            ["startGlucose"] = Glucose(e.Metrics.StartGlucose, useMmol),
            ["peakRise"] = Glucose(e.Metrics.PeakRise, useMmol),
            ["minutesToPeak"] = e.Metrics.MinutesToPeak,
            ["minutesBackToStart"] = e.Metrics.MinutesBackToStart,
            ["drop"] = Glucose(e.Metrics.Drop, useMmol),
            ["nadir"] = Glucose(e.Metrics.Nadir, useMmol),
            ["minutesToNadir"] = e.Metrics.MinutesToNadir,
            ["netInsulin"] = Round(e.Metrics.NetInsulin),
            ["gramsPerUnit"] = Round(e.Metrics.GramsPerUnit, 1),
            ["summary"] = EventSentence(e, useMmol),
        }),
    ];

    /// <summary>
    /// The one sentence the view hands the assistant for a selected event: what its figures show,
    /// in the display unit, with nothing the figures do not say.
    /// </summary>
    internal static string EventSentence(EventResponseEvent e, bool useMmol)
    {
        var unit = useMmol ? "mmol/L" : "mg/dL";
        string G(double mgdl) => Number(Glucose(mgdl, useMmol)!.Value, useMmol ? "0.0" : "0");
        string M(double minutes) => Number(minutes, "0");
        var m = e.Metrics;

        var text = new StringBuilder();
        text.Append(e.Anchor switch
        {
            EventAnchorKind.Meal => e.Grams is { } grams ? $"Meal of {Number(grams)} g" : "Meal",
            EventAnchorKind.CorrectionBolus => "Correction bolus",
            EventAnchorKind.SensorStart => "Sensor start",
            EventAnchorKind.ExerciseStart => "Exercise start",
            EventAnchorKind.OverrideStart => "Override start",
            EventAnchorKind.TemporaryTargetStart => "Temporary target start",
            EventAnchorKind.UnannouncedRise => "Unannounced rise",
            _ => e.Anchor.ToString(),
        });
        text.Append($" on {e.Date} at {e.Time}");

        var clauses = new List<string>();
        if (m.StartGlucose is { } start)
            clauses.Add($"started at {G(start)} {unit}");
        if (e.Response == EventResponseKind.Rise)
        {
            if (m.PeakRise is { } rise && m.MinutesToPeak is { } toPeak)
                clauses.Add(rise >= 0
                    ? $"rose {G(rise)} {unit} to its peak {M(toPeak)} minutes later"
                    : $"did not rise above the start (highest {G(-rise)} {unit} under it)");
            clauses.Add(m.MinutesBackToStart is { } back
                ? back == 0 ? "stayed near the start" : $"was back near the start after {M(back)} minutes"
                : "was not back near the start by the end of the window");
        }
        else
        {
            if (m.Drop is { } drop)
                clauses.Add(drop >= 0 ? $"fell {G(drop)} {unit} by the end of the window" : $"was {G(-drop)} {unit} higher at the end of the window");
            if (m.Nadir is { } nadir && m.MinutesToNadir is { } toNadir)
                clauses.Add($"was lowest at {G(nadir)} {unit} after {M(toNadir)} minutes");
            if (m.WentBelowRange == true)
                clauses.Add("went below range");
        }
        if (clauses.Count > 0)
            text.Append(": ").Append(Join(clauses));
        text.Append('.');
        if (m.NetInsulin is { } net)
            text.Append($" Insulin above the basal schedule: {Number(net, "0.0#")} U.");
        return text.ToString();
    }

    private static string Join(IReadOnlyList<string> clauses) =>
        clauses.Count == 1 ? clauses[0] : string.Join(", ", clauses.Take(clauses.Count - 1)) + ", and " + clauses[^1];

    internal static string Summarise(
        EventResponseReport report,
        IReadOnlyDictionary<int, string> labels,
        IReadOnlyDictionary<int, int> number,
        IReadOnlyList<string> notes,
        bool useMmol)
    {
        const string what = "Meals & events";
        if (NoTimeZone(report, what) is { } noZone)
            return string.Join('\n', [noZone, .. notes]);

        var lines = new List<string>(Context(report, what))
        {
            $"Window: {report.Definition.MinutesBefore} minutes before to {report.Definition.MinutesAfter} minutes after each event."
                + (report.Definition.IsolatedOnly ? " Isolated events only." : ""),
        };
        lines.AddRange(notes);
        if (report.Definition.Cohorts.Count == 0)
        {
            lines.Add("This token's scopes show no cohort.");
            return string.Join('\n', lines);
        }

        var unit = useMmol ? "mmol/L" : "mg/dL";
        string G(double mgdl) => Number(Glucose(mgdl, useMmol)!.Value, useMmol ? "0.0" : "0");
        string Signed(double mgdl) => (mgdl >= 0 ? "+" : "-") + G(Math.Abs(mgdl));

        foreach (var cohort in report.Definition.Cohorts)
        {
            var result = report.Primary.Cohorts.FirstOrDefault(c => c.Cohort == cohort.Cohort);
            var label = $"Cohort {number.GetValueOrDefault(cohort.Cohort, cohort.Cohort)} ({labels[cohort.Cohort]})";
            if (result is null)
            {
                lines.Add($"{label}: no data.");
                continue;
            }

            lines.Add($"{label}: {Plural(result.Events, "event")}.");
            var dropped = new List<string>();
            if (result.NotIsolated > 0) dropped.Add($"{result.NotIsolated} with another event in the window");
            if (result.MissingAnchorGlucose > 0) dropped.Add($"{result.MissingAnchorGlucose} without a reading at the time");
            if (result.BeforeVisibleHistory > 0) dropped.Add($"{result.BeforeVisibleHistory} that reach back before the history this token reads");
            if (dropped.Count > 0)
                lines.Add($"  Not counted: {string.Join("; ", dropped)}.");

            if (cohort.HasFoodFilter)
                lines.Add(
                    $"  Food filter: {Plural(result.MealsWithFoodRecord, "meal")} carried a food record, and {result.Anchors} of them matched it. "
                    + $"{Plural(result.MealsWithoutFoodRecord, "meal")} had only carbs, with no food record, so the filter could not apply to "
                    + $"{(result.MealsWithoutFoodRecord == 1 ? "it" : "them")} and {(result.MealsWithoutFoodRecord == 1 ? "it is" : "they are")} not counted.");

            var metrics = result.Metrics;
            if (result.Events == 0)
                continue;
            if (result.Response == EventResponseKind.Rise)
            {
                if (metrics.PeakRise is { } rise && metrics.MinutesToPeak is { } toPeak)
                    lines.Add($"  Median peak rise {Signed(rise.P50)} {unit}, {Number(toPeak.P50, "0")} minutes after the event "
                        + $"(middle half {Signed(rise.P25)} to {Signed(rise.P75)} {unit}).");
                var back = metrics.MinutesBackToStart?.N ?? 0;
                lines.Add($"  Back near the start within the window: {back} of {result.Events}"
                    + $" ({Number(100.0 * back / result.Events, "0")}%).");
            }
            else
            {
                if (metrics.Drop is { } drop)
                    lines.Add($"  Median drop {G(drop.P50)} {unit} by the end of the window (middle half {G(drop.P25)} to {G(drop.P75)} {unit}).");
                if (metrics.Nadir is { } nadir && metrics.MinutesToNadir is { } toNadir)
                    lines.Add($"  Median lowest reading {G(nadir.P50)} {unit}, {Number(toNadir.P50, "0")} minutes after the event.");
                if (result.WentBelowRangeObserved > 0)
                    lines.Add($"  Went below range: {result.WentBelowRange} of {result.WentBelowRangeObserved} with readings after the event.");
            }

            if (report.Comparison?.Cohorts.FirstOrDefault(c => c.Cohort == cohort.Cohort) is { } compared)
            {
                var peak = compared.Metrics.PeakRise is { } r && compared.Response == EventResponseKind.Rise
                    ? $", median peak rise {Signed(r.P50)} {unit}"
                    : compared.Metrics.Drop is { } d ? $", median drop {G(d.P50)} {unit}" : "";
                lines.Add($"  Comparison range: {Plural(compared.Events, "event")}{peak}.");
            }
        }
        return string.Join('\n', lines);
    }

    /// <summary>Each cohort's events, with its median peak rise (or drop) as the note.</summary>
    internal static IReadOnlyList<ReportFact> FactsOf(EventResponseReport report, IReadOnlyDictionary<int, string> labels, bool useMmol)
    {
        if (report.TimeZoneUnavailableReason is not null)
            return [];
        var unit = ReportFacts.GlucoseUnit(useMmol);
        string G(double mgdl) => GlucoseSummary.Format(Math.Abs(mgdl), useMmol);
        return
        [
            .. report.Definition.Cohorts.Select(cohort =>
            {
                var result = report.Primary.Cohorts.FirstOrDefault(c => c.Cohort == cohort.Cohort);
                var events = result?.Events ?? 0;
                string? note = null;
                if (result is { Events: > 0 } && result.Response == EventResponseKind.Rise && result.Metrics.PeakRise is { } rise
                    && result.Metrics.MinutesToPeak is { } toPeak)
                    note = $"median rise {(rise.P50 >= 0 ? "+" : "-")}{G(rise.P50)} {unit} at {Number(toPeak.P50, "0")} min";
                else if (result is { Events: > 0 } && result.Metrics.Drop is { } drop)
                    note = $"median drop {G(drop.P50)} {unit}";
                return new ReportFact(labels[cohort.Cohort], events.ToString(CultureInfo.InvariantCulture), events == 1 ? "event" : "events", note);
            }),
        ];
    }

    internal static string WindowSentence(EventResponseDefinition definition) =>
        $"Each event from {definition.MinutesBefore} minutes before to {definition.MinutesAfter} minutes after it"
        + (definition.IsolatedOnly ? ", isolated events only." : ".");

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
