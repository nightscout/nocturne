using System.Globalization;
using Nocturne.API.Reports.Derived;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;
using static Nocturne.API.Reports.Datasets.LoopAnalysisDatasetRun;

namespace Nocturne.API.Reports.Datasets;

/// <summary>One figure of each event: its field, words and unit, and how it is read off the event in the display unit.</summary>
internal sealed record EventOutcomeInfo(EventOutcome Outcome, string Label, string Unit, string Description, Func<EventMetrics, double?> Get, bool Insulin = false)
{
    public string Name => ReportWire.Name(Outcome);

    public bool IsGlucose => Unit == ReportField.GlucoseUnit;

    public double? Value(EventResponseEvent e, bool useMmol) =>
        Get(e.Metrics) is { } v ? IsGlucose ? Glucose(v, useMmol) : v : null;

    public string UnitIn(bool useMmol) => IsGlucose ? ReportFacts.GlucoseUnit(useMmol) : Unit;

    public string Format(double value, bool useMmol) =>
        Number(value, IsGlucose ? useMmol ? "0.0" : "0" : "0.##");

    public static readonly IReadOnlyList<EventOutcomeInfo> All =
    [
        new(EventOutcome.PeakRise, "Peak rise", ReportField.GlucoseUnit, "The highest reading after the event minus the glucose at the event.", m => m.PeakRise),
        new(EventOutcome.MinutesToPeak, "Time to peak", "min", "Minutes from the event to the highest reading.", m => m.MinutesToPeak),
        new(EventOutcome.MinutesBackToStart, "Back to start", "min",
            "Minutes until glucose came back near the start; empty when it had not by the window's end.", m => m.MinutesBackToStart),
        new(EventOutcome.Drop, "Drop", ReportField.GlucoseUnit, "The glucose at the event minus the reading at the window's end.", m => m.Drop),
        new(EventOutcome.Nadir, "Lowest", ReportField.GlucoseUnit, "The lowest reading after the event.", m => m.Nadir),
        new(EventOutcome.MinutesToNadir, "Time to lowest", "min", "Minutes from the event to the lowest reading.", m => m.MinutesToNadir),
        new(EventOutcome.GlucoseAt2Hours, "Glucose at 2 hours", ReportField.GlucoseUnit, "The reading nearest two hours after the event.", m => m.GlucoseAt2Hours),
        new(EventOutcome.StartGlucose, "Glucose at the event", ReportField.GlucoseUnit, "The reading nearest the event, within 5 minutes.", m => m.StartGlucose),
        new(EventOutcome.NetInsulin, "Net insulin", "U", "Insulin above the basal schedule over the window.", m => m.NetInsulin, Insulin: true),
        new(EventOutcome.GramsPerUnit, "Carbs per unit", "g/U", "An isolated meal's carbs over its net insulin.", m => m.GramsPerUnit, Insulin: true),
    ];

    public static EventOutcomeInfo Of(EventOutcome outcome) => All.Single(o => o.Outcome == outcome);
}

/// <summary>
/// Which events each per-event figure is worked out for, as the catalogue states it
/// (<c>x-eventFigures</c> on every event dataset's parameters), so a builder reads it rather than
/// copying the engine's rules: the rise figures for a rise anchor and the fall figures for a fall
/// one (<see cref="EventAnchors.ResponseOf"/>), a meal's grams and its carbs per unit for meals only.
/// </summary>
internal static class EventFigures
{
    private static readonly IReadOnlyDictionary<string, Func<EventAnchorKind, bool>> AppliesTo =
        new Dictionary<string, Func<EventAnchorKind, bool>>
        {
            ["peakRise"] = Rise, ["minutesToPeak"] = Rise, ["minutesBackToStart"] = Rise,
            ["drop"] = Fall, ["nadir"] = Fall, ["minutesToNadir"] = Fall,
            ["glucoseAt2Hours"] = Any, ["startGlucose"] = Any, ["netInsulin"] = Any,
            ["gramsPerUnit"] = Meal, ["grams"] = Meal,
            ["minutesSinceMeal"] = Any, ["iobAtEvent"] = Any, ["timeOfDay"] = Any,
        };

    private static bool Rise(EventAnchorKind a) => EventAnchors.ResponseOf(a) == EventResponseKind.Rise;
    private static bool Fall(EventAnchorKind a) => EventAnchors.ResponseOf(a) == EventResponseKind.Fall;
    private static bool Any(EventAnchorKind a) => true;
    private static bool Meal(EventAnchorKind a) => a == EventAnchorKind.Meal;

    /// <summary>
    /// <c>responses</c>: each anchor's kind, <c>rise</c> or <c>fall</c>; <c>figures</c>: each figure's
    /// field name (and <see cref="EventOutcome"/> wire name) with the anchors it is worked out for.
    /// </summary>
    public static System.Text.Json.Nodes.JsonObject Node()
    {
        var anchors = Enum.GetValues<EventAnchorKind>();
        return new System.Text.Json.Nodes.JsonObject
        {
            ["responses"] = new System.Text.Json.Nodes.JsonObject(anchors.Select(a =>
                KeyValuePair.Create(a.ToString(), (System.Text.Json.Nodes.JsonNode?)(Rise(a) ? "rise" : "fall")))),
            ["figures"] = new System.Text.Json.Nodes.JsonObject(AppliesTo.Select(f =>
                KeyValuePair.Create(f.Key, (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonArray(
                    [.. anchors.Where(f.Value).Select(a => (System.Text.Json.Nodes.JsonNode?)a.ToString())])))),
        };
    }
}

/// <summary>
/// A dataset that draws the events of the Meals and events groups one by one, from the event list
/// (<see cref="EventResponseDataset.ComputeAsync"/>). The list places meals, boluses and starts in
/// time, so the endpoint lists it only with <c>treatments.read</c>; without it these datasets draw nothing.
/// The comparison range's events are not listed, so it takes none.
/// </summary>
internal abstract class EventListDataset : IReportDataset
{
    public abstract string Name { get; }

    public abstract string Label { get; }

    public abstract string Description { get; }

    public abstract Type ParametersType { get; }

    public string RequiredScope => Scope.GlucoseRead;

    public abstract IReadOnlyList<ReportField> Fields { get; }

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [];

    public abstract IReadOnlyList<ReportMark> Marks { get; }

    public abstract ReportLayerBindings? DefaultBindings(ReportMark mark);

    public TimeSpan DefaultRange => TimeSpan.FromDays(30);

    public TimeSpan MaxRange => LoopAnalysisDatasetRun.MaxRange;

    public int PointBudget => EventResponseDataset.MaxEvents;

    /// <summary>A count of the events of group <paramref name="group"/> (1 to the most groups), as a bar field.</summary>
    internal static string EventsField(int group) => $"events{group}";

    internal static IEnumerable<ReportField> EventCountFields(string what) =>
        Enumerable.Range(1, EventResponseParameters.MaxGroups).Select(n => new ReportField(
            EventsField(n), $"Group {n}", ReportFieldKind.Number, null, $"Events of group {n} {what}; empty for a group not asked for.",
            Scope.TreatmentsRead, EventResponseDataset.GroupHue(n), Aggregate: ReportAggregate.Sum));

    public virtual IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters)
    {
        var p = (EventListParameters)parameters;
        foreach (var error in new EventResponseDataset().Validate(p))
            yield return error;
        if (p.ComparisonRange is not null)
            yield return ("comparisonRange", $"{Label} shows the events of the range only. Remove the comparison range, or draw it with Meals and events.");
        if (p.Group is { } group && (group < 1 || group > p.AppliedCohorts.Count))
            yield return ("group", p.AppliedCohorts.Count == 1
                ? "There is one group. Send 1, or remove the group."
                : $"Send a group number from 1 to {p.AppliedCohorts.Count}.");
    }

    public abstract Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken);

    /// <summary>The groups drawn, by their number as asked: every group shown, or the one <see cref="EventListParameters.Group"/> names.</summary>
    internal static IReadOnlyList<int> Shown(EventResponseDataset.EventRun run, EventListParameters parameters) =>
        [.. run.Number.Values.Order().Where(n => parameters.Group is not { } only || n == only)];

    /// <summary>The events of the groups drawn, each once, and the groups that counted each, by their number as asked.</summary>
    internal static List<(EventResponseEvent Event, IReadOnlyList<int> Groups)> Events(EventResponseDataset.EventRun run, IReadOnlyList<int> shown) =>
    [
        .. run.Report.Primary.Events
            .Select(e => (Event: e, Groups: (IReadOnlyList<int>)[.. e.Cohorts.Select(c => run.Number.GetValueOrDefault(c, c)).Where(shown.Contains).Order()]))
            .Where(e => e.Groups.Count > 0),
    ];

    /// <summary>The note for an event list the caller's scopes withheld, or null.</summary>
    internal static string? Withheld(EventResponseReport report) =>
        report.WithheldSeries.Contains(LoopAnalysisSeries.EventList) && report.Definition.Cohorts.Count > 0
            ? "The events are not drawn one by one: the event list needs the treatments.read scope, which this token does not hold."
            : null;

    /// <summary>The panel's full text: the groups as Meals and events states them, then the notes of this view.</summary>
    internal static string Summary(EventResponseDataset.EventRun run, bool useMmol, IEnumerable<string> lines) =>
        string.Join('\n', new[] { EventResponseDataset.Summarise(run.Report, run.Labels, run.Number, run.Notes, useMmol) }.Concat(lines));

    internal static ReportDatasetOutput Output(
        EventResponseDataset.EventRun run, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string summary,
        IReadOnlyList<ReportFact> facts, string sentence, IReadOnlyList<ReportField> fields, int counted) => new()
    {
        Rows = rows,
        Summary = summary,
        Facts = facts,
        Sentence = NoTimeZone(run.Report, "Meals & events") ?? sentence,
        Context = ContextLine(run.Report, ReportFacts.Plural(counted, "event")),
        Omitted = run.Report.WithheldSeries.Contains(LoopAnalysisSeries.EventList)
            ? [.. fields.Where(f => f.Scope == Scope.TreatmentsRead).Select(f => new ReportOmittedField(f.Name, Scope.TreatmentsRead))]
            : [],
        Window = Window(run.Report),
        Cohorts = [.. run.AskedLabels.Select((label, i) => new ReportCohort(i + 1, label))],
        FieldLabels = Enumerable.Range(1, run.AskedLabels.Count).ToDictionary(EventsField, n => run.AskedLabels[n - 1]),
    };
}

/// <summary>
/// Each event as a point: one figure up against the state at the event along the axis, the
/// least-squares line of the figure on that state with it, for one group or every group.
/// </summary>
/// <remarks>
/// A dataset of its own rather than more axes on <see cref="EventResponseDataset"/>: its rows are
/// events, not five-minute slots from them, so its fields, marks and default bindings differ, and a
/// derived source of Meals and events keeps reading slots.
/// </remarks>
internal sealed class EventOutcomesDataset : EventListDataset
{
    public override string Name => EventOutcomesParameters.Name;

    public override string Label => "Each event";

    public override string Description =>
        "Each event of the Meals and events groups as a point: a figure such as the peak rise against the glucose, carbs, IOB, "
        + "minutes since the last carbs or time of day at the event, with a least-squares line.";

    public override Type ParametersType => typeof(EventOutcomesParameters);

    /// <summary>The outcomes a point may show; the glucose at the event lies along the axis instead.</summary>
    private static readonly IReadOnlyList<EventOutcomeInfo> Outcomes = [.. EventOutcomeInfo.All.Where(o => o.Outcome != EventOutcome.StartGlucose)];

    internal static string FitField(EventOutcomeInfo outcome) => outcome.Name + "Fit";

    private static readonly IReadOnlyList<ReportField> AxisFields =
    [
        new("startGlucose", "Glucose at the event", ReportFieldKind.Value, ReportField.GlucoseUnit, "The reading nearest the event, within 5 minutes.", Scope.TreatmentsRead),
        new("grams", "Carbs", ReportFieldKind.Value, "g", "A meal's carbs; empty for other events.", Scope.TreatmentsRead),
        new("minutesSinceMeal", "Minutes since carbs", ReportFieldKind.Value, "min",
            "Minutes from the last carbs logged before the event; empty where none were read.", Scope.TreatmentsRead),
        new("iobAtEvent", "IOB at the event", ReportFieldKind.Value, "U", "Insulin on board at the event; empty where no IOB value stood.", Scope.TreatmentsRead),
        new("timeOfDay", "Time of day", ReportFieldKind.TimeOfDay, "min", "The event's local time, minutes after midnight.", Scope.TreatmentsRead),
    ];

    public override IReadOnlyList<ReportField> Fields { get; } =
    [
        .. AxisFields,
        .. Outcomes.Select(o => new ReportField(o.Name, o.Label, ReportFieldKind.Number, o.Unit, o.Description, Scope.TreatmentsRead,
            o.Insulin ? ChartColor.InsulinBolus : null, Aggregate: ReportAggregate.Mean,
            Setting: o.Insulin ? ReportSetting.ProfileRelative : ReportSetting.None)),
        .. Outcomes.Select(o => new ReportField(FitField(o), $"{o.Label}: least-squares line", ReportFieldKind.Number, o.Unit,
            $"The least-squares line of {o.Label.ToLowerInvariant()} on the value the chart lies along, at the event.", Scope.TreatmentsRead,
            ChartColor.MutedForeground, Aggregate: ReportAggregate.Mean,
            Setting: o.Insulin ? ReportSetting.ProfileRelative : ReportSetting.None)),
        new("groups", "Groups", ReportFieldKind.Category, null, "The groups that count the event, by number.", Scope.TreatmentsRead, Companion: true),
        new(ReportField.SentenceField, "Sentence", ReportFieldKind.Category, null, "One sentence that states the event's figures.", Companion: true),
    ];

    public override IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Points];

    public override ReportLayerBindings? DefaultBindings(ReportMark mark) =>
        mark == ReportMark.Points ? new ReportLayerBindings { X = "startGlucose", Y = ["peakRise"], Fit = "peakRiseFit" } : null;

    public override async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var parameters = (EventOutcomesParameters)request.Parameters;
        var useMmol = request.UseMmol;
        var run = await EventResponseDataset.ComputeAsync(request, parameters, cancellationToken);
        var shown = Shown(run, parameters);
        var events = Events(run, shown);
        var lines = new List<string>();
        if (Withheld(run.Report) is { } withheld)
            lines.Add(withheld);

        var rows = events.Take(EventResponseDataset.MaxEvents).Select(e => Row(e.Event, e.Groups, useMmol)).ToList();
        var x = AxisFields.FirstOrDefault(f => f.Name == request.X);
        var bound = Outcomes.FirstOrDefault(o => request.Fields?.Contains(o.Name) ?? o.Outcome == EventOutcome.PeakRise) ?? Outcomes[0];
        string? fitSentence = null;
        var facts = new List<ReportFact>();
        if (x is not null)
        {
            var missing = rows.Count(r => r[x.Name] is null);
            rows = [.. rows.Where(r => r[x.Name] is not null).OrderBy(r => Convert.ToDouble(r[x.Name], CultureInfo.InvariantCulture))];
            if (missing > 0)
                lines.Add($"{ReportFacts.Plural(missing, "event")} with no value of {Lower(x.Label)} are not drawn.");
            foreach (var outcome in Outcomes)
            {
                var line = Fit(rows, x.Name, outcome, out var pairs, out var r);
                foreach (var row in rows)
                    row[FitField(outcome)] = line is null ? null : Math.Round(line.At(Convert.ToDouble(row[x.Name], CultureInfo.InvariantCulture)), 4);
                if (outcome == bound)
                {
                    fitSentence = FitSentence(outcome, x.ResolveUnit(useMmol), line, r, pairs, useMmol, facts);
                    facts = [.. facts.Select(f => f.Of(outcome.Name, x.Name))];
                    lines.Add(fitSentence);
                }
            }
        }

        foreach (var group in shown.Take(ReportFacts.Max - facts.Count))
            facts.Add(ReportFacts.Count(run.AskedLabels[group - 1], events.Count(e => e.Groups.Contains(group)), "events")
                .Of(string.Join('|', Outcomes.Select(o => o.Name))));
        if (events.Count > EventResponseDataset.MaxEvents)
            lines.Add($"The chart shows the first {EventResponseDataset.MaxEvents} of {events.Count} events.");

        return Output(run, [.. rows], Summary(run, useMmol, lines), facts,
            fitSentence ?? $"{ReportFacts.Plural(events.Count, "event")}, each drawn as a point.", Fields, events.Count) with
        {
            Color = parameters.Group is { } only ? EventResponseDataset.GroupHue(only) : null,
        };
    }

    private static Dictionary<string, object?> Row(EventResponseEvent e, IReadOnlyList<int> groups, bool useMmol)
    {
        var row = new Dictionary<string, object?>
        {
            ["startGlucose"] = Round(Glucose(e.Metrics.StartGlucose, useMmol), 1),
            ["grams"] = e.Grams,
            ["minutesSinceMeal"] = e.MinutesSinceMeal,
            ["iobAtEvent"] = Round(e.Metrics.IobAtAnchor),
            ["timeOfDay"] = TimeOnly.TryParseExact(e.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.Hour * 60 + at.Minute : null,
            ["groups"] = string.Join(", ", groups),
            [ReportField.SentenceField] = EventResponseDataset.EventSentence(e, useMmol),
        };
        foreach (var outcome in Outcomes)
            row[outcome.Name] = Round(outcome.Value(e, useMmol), 2);
        return row;
    }

    private static DerivedMath.Line? Fit(
        IEnumerable<Dictionary<string, object?>> rows, string x, EventOutcomeInfo outcome, out int pairs, out double? r)
    {
        List<(double X, double Y)> points =
        [
            .. rows.Where(row => row[x] is not null && row[outcome.Name] is not null)
                .Select(row => (Convert.ToDouble(row[x], CultureInfo.InvariantCulture), Convert.ToDouble(row[outcome.Name], CultureInfo.InvariantCulture))),
        ];
        pairs = points.Count;
        r = DerivedMath.Pearson(points);
        return DerivedMath.LeastSquares(points);
    }

    /// <summary>A label inside a sentence: "Time of day" as "time of day", "IOB at the event" kept.</summary>
    internal static string Lower(string label) =>
        label.Length > 1 && char.IsLower(label[1]) ? char.ToLowerInvariant(label[0]) + label[1..] : label;

    /// <summary>The scatter op's sentence (<see cref="ScatterOp"/>), for an outcome against the state at the event.</summary>
    internal static string FitSentence(
        EventOutcomeInfo outcome, ReportField x, DerivedMath.Line? line, double? r, int pairs, bool useMmol, List<ReportFact> facts)
    {
        var unit = outcome.UnitIn(useMmol);
        var words = $"{outcome.Label} against {Lower(x.Label)}";
        facts.Add(ReportFacts.Count("Events with both", pairs));
        if (r is not { } rValue || line is null)
            return $"{words}: a line needs at least {DerivedMath.MinPairs} events that vary; there {(pairs == 1 ? "is" : "are")} {ReportFacts.Plural(pairs, "event")} with both.";
        var slopeUnit = ReportDerivedOps.RatioUnit(unit, x.Unit);
        facts.Insert(0, new ReportFact("r", ReportDerivedOps.N(rValue, "0.00")));
        facts.Add(new ReportFact("Slope", ReportDerivedOps.N(line.Slope, "0.###"), slopeUnit));
        return $"{words}: r = {ReportDerivedOps.N(rValue, "0.00")} over {ReportFacts.Plural(pairs, "event")}. "
            + $"The least-squares line has a slope of {ReportDerivedOps.WithUnit(line.Slope, slopeUnit, "0.###")} "
            + $"and an intercept of {ReportDerivedOps.WithUnit(line.Intercept, unit)}.";
    }
}

/// <summary>
/// The spread of one figure in each group: the events counted in equal ranges of the figure, a bar
/// per group in each range, with each group's median and middle half as its figures.
/// </summary>
internal sealed class EventOutcomeSpreadDataset : EventListDataset
{
    /// <summary>The most ranges the figure is cut into; the width is a round number that keeps them under it.</summary>
    public const int MaxBins = 12;

    public override string Name => EventOutcomeSpreadParameters.Name;

    public override string Label => "Event outcome spread";

    public override string Description =>
        "How one figure of each event, such as the peak rise, is spread in each Meals and events group: events counted in ranges of the figure.";

    public override Type ParametersType => typeof(EventOutcomeSpreadParameters);

    public override IReadOnlyList<ReportField> Fields { get; } =
    [
        new("range", "Range", ReportFieldKind.Category, null, "A range of the figure, from its lower end up to its upper end.", Scope.TreatmentsRead),
        .. EventCountFields("whose figure is in the range"),
        new(ReportField.SentenceField, "Sentence", ReportFieldKind.Category, null, "One sentence that states the range's counts.", Companion: true),
    ];

    public override IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Bars];

    public override ReportLayerBindings? DefaultBindings(ReportMark mark) =>
        mark == ReportMark.Bars
            ? new ReportLayerBindings { X = "range", Y = [.. Enumerable.Range(1, EventResponseParameters.MaxGroups).Select(EventsField)] }
            : null;

    public override async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var parameters = (EventOutcomeSpreadParameters)request.Parameters;
        var useMmol = request.UseMmol;
        var outcome = EventOutcomeInfo.Of(parameters.Outcome);
        var run = await EventResponseDataset.ComputeAsync(request, parameters, cancellationToken);
        var shown = Shown(run, parameters);
        var events = Events(run, shown);
        var lines = new List<string>();
        if (Withheld(run.Report) is { } withheld)
            lines.Add(withheld);

        var values = shown.ToDictionary(g => g, g => events
            .Where(e => e.Groups.Contains(g)).Select(e => outcome.Value(e.Event, useMmol)).OfType<double>().ToList());
        var unit = outcome.UnitIn(useMmol);
        var rows = Rows(values, outcome, unit, run.AskedLabels, useMmol);

        var facts = new List<ReportFact>();
        var sentences = new List<string>();
        foreach (var group in shown.Take(ReportFacts.Max))
        {
            var label = run.AskedLabels[group - 1];
            if (SlotSummary.Of(values[group]) is not { } spread)
            {
                facts.Add(ReportFacts.Count(label, 0, "events").Of(EventsField(group)));
                sentences.Add($"{label}: no event with a {outcome.Label.ToLowerInvariant()}.");
                continue;
            }
            string F(double v) => outcome.Format(v, useMmol);
            var note = $"middle half {F(spread.P25)} to {F(spread.P75)} {unit}, {ReportFacts.Plural(spread.N, "event")}";
            facts.Add(new ReportFact(label, F(spread.P50), unit, "median, " + note).Of(EventsField(group)));
            sentences.Add($"{label}: median {F(spread.P50)} {unit}, {note}.");
        }
        var sentence = $"{outcome.Label} of each event. {string.Join(' ', sentences)}";
        lines.Add(sentence);
        lines.Add("Each range holds its lower end and not its upper end.");
        var missing = events.Count - values.Values.SelectMany(v => v).Count();
        if (shown.Count == 1 && missing > 0)
            lines.Add($"{ReportFacts.Plural(missing, "event")} have no {outcome.Label.ToLowerInvariant()} and are not counted.");

        return Output(run, rows, Summary(run, useMmol, lines), facts, sentence, Fields, events.Count);
    }

    /// <summary>The ranges, lowest first, every one between the lowest and highest value, so an empty range shows as one.</summary>
    internal static List<IReadOnlyDictionary<string, object?>> Rows(
        IReadOnlyDictionary<int, List<double>> values, EventOutcomeInfo outcome, string unit, IReadOnlyList<string> labels, bool useMmol)
    {
        var all = values.Values.SelectMany(v => v).ToList();
        if (all.Count == 0)
            return [];
        var width = Width(all.Min(), all.Max());
        var first = Math.Floor(all.Min() / width) * width;
        var count = (int)Math.Floor((all.Max() - first) / width) + 1;
        string F(double v) => Number(Math.Round(v, 6), outcome.IsGlucose && useMmol ? "0.0" : "0.##");
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        for (var i = 0; i < count; i++)
        {
            var low = first + i * width;
            var high = low + width;
            var range = $"{F(low)} to {F(high)} {unit}";
            var row = new Dictionary<string, object?> { ["range"] = range };
            var counts = new List<string>();
            foreach (var (group, list) in values)
            {
                var n = list.Count(v => v >= low - 1e-9 && v < high - 1e-9);
                row[EventsField(group)] = n;
                counts.Add($"{labels[group - 1]} {ReportFacts.Plural(n, "event")}");
            }
            row[ReportField.SentenceField] = $"{outcome.Label} from {F(low)} up to {F(high)} {unit}: {string.Join(", ", counts)}.";
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>A round width (1, 2 or 5 times a power of ten) that cuts the values into at most <see cref="MaxBins"/> ranges.</summary>
    internal static double Width(double min, double max)
    {
        var span = max - min;
        if (span <= 0)
            return Math.Max(1e-6, Math.Pow(10, Math.Floor(Math.Log10(Math.Max(Math.Abs(max), 1)))) / 10);
        var raw = span / (MaxBins - 1);
        var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        return new[] { 1, 2, 5, 10 }.Select(m => m * power).First(w => w >= raw);
    }
}

/// <summary>When the events of each group happen: counted by local hour of the day or, along the weekday, by local weekday.</summary>
internal sealed class EventOccurrenceDataset : EventListDataset
{
    public override string Name => EventOccurrenceParameters.Name;

    public override string Label => "When events happen";

    public override string Description => "The events of each Meals and events group counted by local hour of the day, or by local weekday.";

    public override Type ParametersType => typeof(EventOccurrenceParameters);

    private static readonly string[] Weekdays = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    public override IReadOnlyList<ReportField> Fields { get; } =
    [
        new("hour", "Hour of the day", ReportFieldKind.TimeOfDay, "min", "Start of the local hour, minutes after midnight.", Scope.TreatmentsRead),
        new("weekday", "Weekday", ReportFieldKind.Category, null, "The local weekday, Mon to Sun.", Scope.TreatmentsRead),
        .. EventCountFields("in the hour or on the weekday"),
        new(ReportField.SentenceField, "Sentence", ReportFieldKind.Category, null, "One sentence that states the hour's or weekday's counts.", Companion: true),
    ];

    public override IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Bars];

    public override ReportLayerBindings? DefaultBindings(ReportMark mark) =>
        mark == ReportMark.Bars
            ? new ReportLayerBindings { X = "hour", Y = [.. Enumerable.Range(1, EventResponseParameters.MaxGroups).Select(EventsField)] }
            : null;

    public override async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var parameters = (EventOccurrenceParameters)request.Parameters;
        var run = await EventResponseDataset.ComputeAsync(request, parameters, cancellationToken);
        var shown = Shown(run, parameters);
        var events = Events(run, shown);
        var byWeekday = request.X == "weekday";
        var lines = new List<string>();
        if (Withheld(run.Report) is { } withheld)
            lines.Add(withheld);

        var keys = events.Select(e => (e.Groups, Key: byWeekday ? WeekdayOf(e.Event) : HourOf(e.Event))).Where(e => e.Key is not null).ToList();
        var slots = byWeekday ? 7 : 24;
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        for (var slot = 0; slot < slots; slot++)
        {
            var words = byWeekday ? $"On {Weekdays[slot]}" : $"From {Clock(slot * 60)} to {Clock((slot + 1) * 60 % 1440)}";
            var row = new Dictionary<string, object?> { ["hour"] = byWeekday ? null : slot * 60, ["weekday"] = byWeekday ? Weekdays[slot] : null };
            var counts = new List<string>();
            foreach (var group in shown)
            {
                var n = keys.Count(k => k.Key == slot && k.Groups.Contains(group));
                row[EventsField(group)] = n;
                counts.Add($"{run.AskedLabels[group - 1]} {ReportFacts.Plural(n, "event")}");
            }
            row[ReportField.SentenceField] = $"{words}: {string.Join(", ", counts)}.";
            rows.Add(row);
        }

        var facts = new List<ReportFact>();
        var sentences = new List<string>();
        foreach (var group in shown.Take(ReportFacts.Max))
        {
            var label = run.AskedLabels[group - 1];
            var mine = keys.Where(k => k.Groups.Contains(group)).GroupBy(k => k.Key!.Value).Select(g => (Slot: g.Key, N: g.Count())).ToList();
            var total = mine.Sum(m => m.N);
            if (total == 0)
            {
                facts.Add(ReportFacts.Count(label, 0, "events").Of(EventsField(group)));
                sentences.Add($"{label}: no events.");
                continue;
            }
            var most = mine.Max(m => m.N);
            var where = string.Join(", ", mine.Where(m => m.N == most).OrderBy(m => m.Slot)
                .Select(m => byWeekday ? Weekdays[m.Slot] : $"{Clock(m.Slot * 60)}-{Clock((m.Slot + 1) * 60 % 1440)}"));
            facts.Add(ReportFacts.Count(label, total, "events", $"most {(byWeekday ? "on" : "in")} {where} ({most})").Of(EventsField(group)));
            sentences.Add($"{label}: {ReportFacts.Plural(total, "event")}, most {(byWeekday ? "on" : "in")} {where} ({most}).");
        }
        var sentence = string.Join(' ', sentences);
        lines.Add((byWeekday ? "By local weekday. " : "By local hour. ") + sentence);

        return Output(run, rows, Summary(run, request.UseMmol, lines), facts, sentence, Fields, events.Count);
    }

    private static int? HourOf(EventResponseEvent e) =>
        TimeOnly.TryParseExact(e.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.Hour : null;

    /// <summary>Monday 0 to Sunday 6.</summary>
    private static int? WeekdayOf(EventResponseEvent e) =>
        DateOnly.TryParseExact(e.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? ((int)day.DayOfWeek + 6) % 7
            : null;
}
