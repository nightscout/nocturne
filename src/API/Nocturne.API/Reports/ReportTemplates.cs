using System.Text.Json;
using Nocturne.API.Reports.Derived;

namespace Nocturne.API.Reports;

/// <summary>
/// The specs the report builder offers as a start, each a stack of layers: the dashboard glucose
/// chart, the AGP report's layout, the treatments of the last day, a two-period comparison, the day's
/// glucose on weekdays against the weekend, and insulin on board against glucose. Each is normalised by
/// <see cref="ReportSpecValidator"/> as a member's spec is, so it reads, degrades, clamps and audits as
/// any other spec does.
/// </summary>
internal static class ReportTemplates
{
    /// <summary>A template before it is normalised.</summary>
    internal sealed record Template(string Id, string Name, string Description, object Spec);

    public static IReadOnlyList<Template> All { get; } =
    [
        new("glucose", "Glucose chart",
            "The last 6 hours as the dashboard shows them: basal, glucose with the target range, boluses and carbs, insulin and carbs on board, and modes.",
            GlucoseSpec(6)),
        new("agp", "Ambulatory glucose profile",
            "The key figures, the glucose pattern over the day and the time in each range for the last 14 days.", AgpSpec(14)),
        new("treatments", "Treatments",
            "Glucose with the boluses and carbs on it, then the treatment records of the last 24 hours.", TreatmentsSpec(24)),
        new("comparison", "Period comparison",
            "The last 14 days beside the 14 days before, figure by figure.", ComparisonSpec()),
        new("weekdays", "Meals, weekday against weekend",
            "The glucose pattern over the day on weekdays and at the weekend, over the last 28 days, on one chart.", WeekdaySpec(28)),
        new("insulin-glucose", "Insulin and glucose",
            "Glucose against insulin on board every 15 minutes, and how the two correlate when one is shifted, over the last 14 days.",
            InsulinGlucoseSpec(14)),
    ];

    /// <summary>Every template, normalised; a template that fails validation is a bug, not the caller's error.</summary>
    public static IReadOnlyList<(Template Template, ReportSpec Spec)> Normalised(ReportSpecValidator validator, bool useMmol) =>
        [.. All.Select(t => (t, Normalise(validator, t.Spec, useMmol)))];

    internal static ReportSpec Normalise(ReportSpecValidator validator, object spec, bool useMmol)
    {
        var validation = validator.Validate(JsonSerializer.SerializeToElement(spec), useMmol);
        return validation.IsValid
            ? validation.Spec!
            : throw new InvalidOperationException("A template spec is not valid: " + string.Join("; ", validation.Errors));
    }

    private static object Glucose => new { name = GlucoseSeriesParameters.Name };

    /// <summary>The dashboard chart of the last <paramref name="hours"/> hours, as four tracks.</summary>
    internal static object GlucoseSpec(int hours) => new
    {
        version = ReportSpec.CurrentVersion,
        title = "Glucose",
        range = new { hours },
        panels = new object[]
        {
            new
            {
                id = "glucose",
                kind = "chart",
                tracks = new object[]
                {
                    new
                    {
                        id = "basal",
                        title = "Basal",
                        height = 0.5,
                        layers = new object[] { new { id = "basal", title = "Basal", dataset = new { name = BasalDeliveryParameters.Name }, mark = "area" } },
                    },
                    new
                    {
                        id = "glucose",
                        title = "Glucose",
                        height = 2,
                        layers = new object[]
                        {
                            new { id = "target", title = "Target range", dataset = Glucose, mark = "band", bindings = new { x = "time", edges = new[] { "targetLow", "targetHigh" } } },
                            new { id = "glucose", title = "Glucose", dataset = Glucose, mark = "line", bindings = new { x = "time", y = new[] { "glucose" } } },
                            new
                            {
                                id = "treatments",
                                title = "Boluses and carbs",
                                dataset = new { name = TreatmentsParameters.Name, types = new[] { "bolus", "carbs" } },
                                mark = "markers",
                                bindings = new { x = "time", y = new[] { "insulin", "carbs" } },
                            },
                        },
                    },
                    new
                    {
                        id = "onboard",
                        title = "On board",
                        height = 0.6,
                        layers = new object[]
                        {
                            new { id = "iob", title = "IOB", dataset = Glucose, mark = "line", bindings = new { x = "time", y = new[] { "iob" } } },
                            new { id = "cob", title = "COB", dataset = Glucose, mark = "line", bindings = new { x = "time", y = new[] { "cob" } } },
                        },
                    },
                    new
                    {
                        id = "states",
                        title = "Modes",
                        layers = new object[] { new { id = "spans", title = "Modes and overrides", dataset = new { name = StateSpansParameters.Name }, mark = "spans" } },
                    },
                },
            },
        },
    };

    /// <summary>
    /// The web AGP report's layout: its figure strip, its 24-hour percentile chart (the hourly
    /// distribution it draws, <c>averagedStats</c>), and its time in range bar.
    /// </summary>
    internal static object AgpSpec(int days) => new
    {
        version = ReportSpec.CurrentVersion,
        title = $"Ambulatory glucose profile, last {days} {(days == 1 ? "day" : "days")}",
        range = new { hours = days * 24 },
        panels = new object[]
        {
            new
            {
                id = "figures",
                title = "Key figures",
                kind = "stat_tiles",
                dataset = new { name = GlucoseStatisticsParameters.Name },
                bindings = new
                {
                    columns = new[] { "timeInRangePercent", "mean", "gmi", "cv", "timeBelow", "timeAbove", "tdd", "basalPercent", "carbsPerDay" },
                },
            },
            new
            {
                id = "profile",
                title = "Glucose pattern over the day",
                kind = "chart",
                tracks = new object[]
                {
                    new { layers = new object[] { new { id = "bands", dataset = new { name = GlucoseDistributionByHourParameters.Name }, mark = "band" } } },
                },
            },
            new
            {
                id = "ranges",
                title = "Time in each glucose range",
                kind = "chart",
                tracks = new object[]
                {
                    new
                    {
                        layers = new object[]
                        {
                            new
                            {
                                id = "ranges",
                                dataset = new { name = GlucoseDistributionParameters.Name },
                                mark = "bars",
                                bindings = new
                                {
                                    x = "period",
                                    y = new[] { "veryLow", "low", "inRange", "high", "veryHigh" },
                                    stacked = true,
                                    orientation = "horizontal",
                                },
                            },
                        },
                    },
                },
            },
        },
    };

    /// <summary>The glucose line with the treatment markers on it, and the treatment records under it.</summary>
    internal static object TreatmentsSpec(int hours) => new
    {
        version = ReportSpec.CurrentVersion,
        title = $"Treatments, last {hours} {(hours == 1 ? "hour" : "hours")}",
        range = new { hours },
        panels = new object[]
        {
            new
            {
                id = "glucose",
                title = "Glucose with boluses and carbs",
                kind = "chart",
                tracks = new object[]
                {
                    new
                    {
                        layers = new object[]
                        {
                            new { id = "glucose", title = "Glucose", dataset = Glucose, mark = "line", bindings = new { x = "time", y = new[] { "glucose" } } },
                            new
                            {
                                id = "markers",
                                title = "Boluses and carbs",
                                dataset = new { name = TreatmentsParameters.Name, types = new[] { "bolus", "carbs" } },
                                mark = "markers",
                            },
                        },
                    },
                },
            },
            new
            {
                id = "treatments",
                title = "Treatment records",
                kind = "table",
                dataset = new { name = TreatmentsParameters.Name },
            },
        },
    };

    /// <summary>The comparison report: one comparison panel over the last 14 days and the 14 days before.</summary>
    internal static object ComparisonSpec() => new
    {
        version = ReportSpec.CurrentVersion,
        title = "Period comparison",
        panels = new object[]
        {
            new
            {
                id = "comparison",
                title = "Two periods side by side",
                kind = "comparison",
                dataset = new { name = PeriodComparisonParameters.Name, preset = "last14-prior14" },
            },
        },
    };

    /// <summary>Two daily profile layers on one chart over the day: Monday to Friday, and Saturday and Sunday.</summary>
    internal static object WeekdaySpec(int days)
    {
        static object Layer(string id, string title, string[] weekdays) => new
        {
            id,
            title,
            dataset = new { name = DailyProfileParameters.Name, dayOfWeek = weekdays },
            mark = "band",
            bindings = new { x = "minuteOfDay", y = new[] { "glucoseP50" }, edges = new[] { "glucoseP25", "glucoseP75" } },
        };
        return new
        {
            version = ReportSpec.CurrentVersion,
            title = $"Weekdays and the weekend, last {days} days",
            range = new { hours = days * 24 },
            panels = new object[]
            {
                new
                {
                    id = "weekdays",
                    title = "Glucose over the day: median and middle half",
                    kind = "chart",
                    tracks = new object[]
                    {
                        new
                        {
                            layers = new object[]
                            {
                                Layer("weekdays", "Monday to Friday", ["Mon", "Tue", "Wed", "Thu", "Fri"]),
                                Layer("weekend", "Saturday and Sunday", ["Sat", "Sun"]),
                            },
                        },
                    },
                },
            },
        };
    }

    /// <summary>A scatter of glucose against insulin on board, and their lag correlation, on a 15-minute grid.</summary>
    internal static object InsulinGlucoseSpec(int days)
    {
        object Source(string field) => new { dataset = Glucose, field };
        return new
        {
            version = ReportSpec.CurrentVersion,
            title = $"Insulin and glucose, last {days} days",
            range = new { hours = days * 24 },
            panels = new object[]
            {
                new
                {
                    id = "scatter",
                    title = "Glucose against insulin on board",
                    kind = "chart",
                    grid = 15,
                    tracks = new object[]
                    {
                        new
                        {
                            layers = new object[]
                            {
                                new { id = "scatter", derived = new { op = ScatterSpec.Name, a = Source("iob"), b = Source("glucose") }, mark = "points" },
                            },
                        },
                    },
                },
                new
                {
                    id = "lag",
                    title = "Correlation with glucose shifted later",
                    kind = "chart",
                    grid = 15,
                    tracks = new object[]
                    {
                        new
                        {
                            layers = new object[]
                            {
                                new { id = "lag", derived = new { op = LagCorrelationSpec.Name, a = Source("iob"), b = Source("glucose") }, mark = "line" },
                            },
                        },
                    },
                },
            },
        };
    }
}
