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

    public string Name => EventResponseParameters.Name;

    public string Description =>
        "Meals & events: up to three cohorts of events (meals, correction boluses, sensor starts, exercise, override and "
        + "temporary target starts, unannounced rises), each narrowed by time of day, weekday and, for meals, by size and by "
        + "the foods a meal carries a food record of (ids from find_foods). Drawn as range_bands: one row per cohort and "
        + "five-minute slot from the event, with glucose and glucose change percentiles (P10, P25, P50, P75, P90) across "
        + "the cohort's events and the median IOB, COB and net loop correction. Drawn as table: one row per event with its "
        + "figures and a one-sentence summary. Reads local days; needs glucose.read, and treatments.read for meal, bolus, "
        + "exercise, override, temporary target and rise cohorts, devices.read for sensor starts, food.read for a food filter.";

    public Type ParametersType => typeof(EventResponseParameters);

    public string RequiredScope => Scope.GlucoseRead;

    private static readonly IReadOnlyList<ReportField> SlotFields =
    [
        new("series", "Series", ReportFieldKind.Category, null, "The cohort and range the row belongs to, as a label."),
        new("cohort", "Cohort", ReportFieldKind.Number, null, "The cohort's number, 1 to 3, in the order the cohorts were sent."),
        new("range", "Range", ReportFieldKind.Category, null, "primary, or comparison for the comparison range."),
        new("minute", "Minutes from event", ReportFieldKind.Offset, "min", "Start of the five-minute slot, minutes from the event; negative before it."),
        new("glucoseP10", "Glucose P10", ReportFieldKind.Number, ReportField.GlucoseUnit, "10th percentile of glucose across the cohort's events in the slot."),
        new("glucoseP25", "Glucose P25", ReportFieldKind.Number, ReportField.GlucoseUnit, "25th percentile of glucose."),
        new("glucoseP50", "Glucose median", ReportFieldKind.Number, ReportField.GlucoseUnit, "Median glucose."),
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

    public ReportBindings? DefaultBindings(ReportPrimitive primitive) => primitive == ReportPrimitive.RangeBands
        ? new ReportBindings
        {
            X = "minute",
            Y = ["glucoseP50"],
            Band = ["glucoseP10", "glucoseP25", "glucoseP75", "glucoseP90"],
            Series = "series",
        }
        : null;

    public IReadOnlyList<ReportPrimitive> AllowedPrimitives { get; } = [ReportPrimitive.RangeBands, ReportPrimitive.Table];

    public TimeSpan DefaultRange => TimeSpan.FromDays(30);

    public TimeSpan MaxRange(ReportPrimitive primitive) => LoopAnalysisDatasetRun.MaxRange;

    public int PointBudget => MaxEvents;

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> SampleRows { get; } =
    [
        new Dictionary<string, object?>
        {
            ["series"] = "Cohort 1: Meal", ["cohort"] = 1, ["range"] = "primary", ["minute"] = 60,
            ["glucoseP10"] = 120.0, ["glucoseP25"] = 140.0, ["glucoseP50"] = 165.0, ["glucoseP75"] = 190.0, ["glucoseP90"] = 215.0,
            ["deltaP10"] = 10.0, ["deltaP25"] = 30.0, ["deltaP50"] = 55.0, ["deltaP75"] = 80.0, ["deltaP90"] = 100.0,
            ["iobP50"] = 2.4, ["cobP50"] = 22.0, ["netLoopCorrectionP50"] = 0.6, ["events"] = 14,
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

        var query = parameters.ToEventQuery([.. kept.Select(k => k.Cohort)]);
        var definition = query.ToDefinition();
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

        var foodNames = await FoodNamesAsync(services, definition, foodRead, cancellationToken);
        var labels = definition.Cohorts.ToDictionary(c => c.Cohort, c => CohortLabel(c, number[c.Cohort], foodNames));
        var slotMinutes = EventResponseCalculator.SlotMinutes(definition);

        var events = report.Primary.Events;
        var counted = report.Primary.Cohorts.Sum(c => c.Events);
        if (counted > 0)
            request.ReadLog.Record("glucose", counted);
        if (events.Count > 0)
            request.ReadLog.Record("treatments", events.Count);

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

        if (events.Count > MaxEvents)
            notes.Add($"The table shows the first {MaxEvents} of {events.Count} events.");

        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = Summarise(report, labels, notes, useMmol),
            Omitted = Omitted(report, fieldSeries),
            Window = Window(report),
        };
    }

    /// <summary>The names of the foods the cohorts filter on, by id, where the token reads foods.</summary>
    private static async Task<IReadOnlyDictionary<Guid, string>> FoodNamesAsync(
        IServiceProvider services, EventResponseDefinition definition, bool foodRead, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string>();
        if (!foodRead)
            return names;
        var foods = services.GetRequiredService<IFoodRepository>();
        foreach (var id in definition.Cohorts.SelectMany(c => c.FoodIds ?? []).Distinct())
        {
            if (await foods.GetFoodByIdAsync(id.ToString(), ct) is { } food)
                names[id] = food.Name;
        }
        return names;
    }

    /// <summary>A cohort's number, kind and filters: "Cohort 2: Meal, 06:00-10:00, Mon Tue, foods Penne or Rice".</summary>
    internal static string CohortLabel(EventCohortDefinition cohort, int number, IReadOnlyDictionary<Guid, string> foodNames)
    {
        var parts = new List<string> { cohort.Anchor.ToString() };
        if (cohort.FromMinute is not null || cohort.ToMinute is not null)
            parts.Add($"{Clock(cohort.FromMinute ?? 0)}-{Clock(cohort.ToMinute ?? 0)}");
        if (cohort.MinGrams is not null || cohort.MaxGrams is not null)
            parts.Add(cohort.MinGrams is { } min && cohort.MaxGrams is { } max ? $"{Number(min)}-{Number(max)} g"
                : cohort.MinGrams is { } atLeast ? $"at least {Number(atLeast)} g" : $"at most {Number(cohort.MaxGrams!.Value)} g");
        if (cohort.DaysOfWeek is { Count: > 0 } days)
            parts.Add(string.Join(' ', days.Select(d => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(d))));
        if (cohort.FoodIds is { Count: > 0 } ids)
            parts.Add("foods " + string.Join(" or ", ids.Select(id => foodNames.GetValueOrDefault(id, id.ToString()))));
        if (cohort.FoodCategory is { } category)
            parts.Add($"food category {category}");
        return $"Cohort {number}: {string.Join(", ", parts)}";
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
            var label = labels[cohort.Cohort];
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

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
