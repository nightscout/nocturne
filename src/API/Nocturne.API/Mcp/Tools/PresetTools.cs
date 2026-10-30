using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nocturne.API.Mcp.Reports;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Mcp.Tools;

/// <summary>
/// Report presets: each builds a spec, has <see cref="ReportSpecValidator"/> normalise it as it
/// normalises a model's, and runs it through <see cref="ReportRenderer"/>, so a preset reads, degrades,
/// clamps and audits exactly as <c>render_report</c> does, and its result can be saved like any other.
/// </summary>
[McpServerToolType]
internal sealed class PresetTools
{
    internal const int MaxAgpDays = 90;

    internal const int MaxTreatmentHours = 72;

    [McpServerTool(Name = "show_agp", Title = "Show AGP", ReadOnly = true, OpenWorld = false)]
    [McpAppUi(ResourceUri = ReportResources.ReportUri)]
    [Description(
        "Shows the ambulatory glucose profile (AGP) for the last few days, laid out as the Nocturne AGP report: the key "
        + "figures (time in range, mean, GMI, CV, below and above range, and with treatments.read insulin and carbs per day), "
        + "the glucose percentile bands over the hours of the day, and the time in each glucose band. Returns the text "
        + "summary of each part.")]
    [McpRequireScope(Scope.GlucoseRead)]
    public static async Task<CallToolResult> ShowAgp(
        ReportSpecValidator validator,
        ReportRenderer renderer,
        [Description("How many days back from now, 1 to 90. Default 14, the standard AGP period.")] int days = 14,
        CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > MaxAgpDays)
            throw new McpInvalidArgumentsException($"days must be between 1 and {MaxAgpDays}.");

        var spec = Normalise(validator, AgpSpec(days), await renderer.UsesMmolAsync(cancellationToken));
        return (await renderer.RenderAsync(spec, cancellationToken)).Result;
    }

    [McpServerTool(Name = "show_treatments", Title = "Show treatments", ReadOnly = true, OpenWorld = false)]
    [McpAppUi(ResourceUri = ReportResources.ReportUri)]
    [Description(
        "Shows the last few hours of glucose with bolus and carb markers on the line, and a table of the treatment "
        + "records (boluses, carbs, temp basals, notes, overrides, injections). Returns the text summary: counts, totals, "
        + "largest and latest of each kind.")]
    [McpRequireScope(Scope.TreatmentsRead)]
    public static async Task<CallToolResult> ShowTreatments(
        ReportSpecValidator validator,
        ReportRenderer renderer,
        [Description("How many hours back from now, 1 to 72. Default 24.")] int hours = 24,
        CancellationToken cancellationToken = default)
    {
        if (hours is < 1 or > MaxTreatmentHours)
            throw new McpInvalidArgumentsException($"hours must be between 1 and {MaxTreatmentHours}.");

        var spec = Normalise(validator, TreatmentsSpec(hours), await renderer.UsesMmolAsync(cancellationToken));
        return (await renderer.RenderAsync(spec, cancellationToken)).Result;
    }

    /// <summary>
    /// The web AGP report's layout as a spec: its figure strip, its 24-hour percentile chart (the hourly
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

    /// <summary>A preset's spec as the validator normalises it; a preset that fails validation is a bug, not the caller's error.</summary>
    internal static ReportSpec Normalise(ReportSpecValidator validator, object spec, bool useMmol)
    {
        var validation = validator.Validate(JsonSerializer.SerializeToElement(spec), useMmol);
        return validation.IsValid
            ? validation.Spec!
            : throw new InvalidOperationException("A preset spec is not valid: " + string.Join("; ", validation.Errors));
    }
}
