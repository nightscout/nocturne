using System.Text.Json;

namespace Nocturne.API.Reports;

/// <summary>
/// The specs the report builder offers as a start: the dashboard glucose chart, the AGP report's
/// layout, the treatments of the last day and a two-period comparison. Each is normalised by
/// <see cref="ReportSpecValidator"/> as a member's spec is, so it reads, degrades, clamps and audits
/// as any other spec does.
/// </summary>
internal static class ReportTemplates
{
    /// <summary>A template before it is normalised.</summary>
    internal sealed record Template(string Id, string Name, string Description, object Spec);

    public static IReadOnlyList<Template> All { get; } =
    [
        new("glucose", "Glucose chart",
            "The dashboard glucose chart of the last 6 hours, with insulin, carbs and basal.", GlucoseSpec(6)),
        new("agp", "Ambulatory glucose profile",
            "The key figures, the glucose pattern over the day and the time in each range for the last 14 days.", AgpSpec(14)),
        new("treatments", "Treatments",
            "Glucose with the boluses and carbs on it, then the treatment records of the last 24 hours.", TreatmentsSpec(24)),
        new("comparison", "Period comparison",
            "The last 14 days beside the 14 days before, figure by figure.", ComparisonSpec()),
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

    /// <summary>One dashboard chart of the last <paramref name="hours"/> hours.</summary>
    internal static object GlucoseSpec(int hours) => new
    {
        version = ReportSpec.CurrentVersion,
        title = "Glucose",
        range = new { hours },
        panels = new object[]
        {
            new { id = "glucose", dataset = new { name = GlucoseSeriesParameters.Name }, primitive = "glucose_chart" },
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
                dataset = new { name = GlucoseStatisticsParameters.Name },
                primitive = "stat_tiles",
                bindings = new
                {
                    columns = new[] { "timeInRangePercent", "mean", "gmi", "cv", "timeBelow", "timeAbove", "tdd", "basalPercent", "carbsPerDay" },
                },
            },
            new
            {
                id = "profile",
                title = "Glucose pattern over the day",
                dataset = new { name = GlucoseDistributionByHourParameters.Name },
                primitive = "range_bands",
            },
            new
            {
                id = "ranges",
                title = "Time in each glucose range",
                dataset = new { name = GlucoseDistributionParameters.Name },
                primitive = "bar",
                bindings = new
                {
                    x = "period",
                    y = new[] { "veryLow", "low", "inRange", "high", "veryHigh" },
                    stacked = true,
                    orientation = "horizontal",
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
                dataset = new { name = GlucoseSeriesParameters.Name, markers = new[] { "bolus", "carbs" } },
                primitive = "time_series",
                bindings = new { x = "time", y = new[] { "glucose" }, band = new[] { "targetLow", "targetHigh" } },
            },
            new
            {
                id = "treatments",
                title = "Treatment records",
                dataset = new { name = TreatmentsParameters.Name },
                primitive = "table",
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
                dataset = new { name = PeriodComparisonParameters.Name, preset = "last14-prior14" },
                primitive = "comparison",
            },
        },
    };
}
