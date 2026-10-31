using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nocturne.API.Extensions;
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
        IHttpContextAccessor httpContextAccessor,
        [Description("How many days back from now, 1 to 90. Default 14, the standard AGP period.")] int days = 14,
        CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > MaxAgpDays)
            throw new McpInvalidArgumentsException($"days must be between 1 and {MaxAgpDays}.");

        var clamped = httpContextAccessor.HttpContext?.IsCallerHistoryClamped() ?? false;
        var spec = Normalise(validator, AgpSpec(days, clamped), await renderer.UsesMmolAsync(cancellationToken));
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
        IHttpContextAccessor httpContextAccessor,
        [Description("How many hours back from now, 1 to 72. Default 24.")] int hours = 24,
        CancellationToken cancellationToken = default)
    {
        if (hours is < 1 or > MaxTreatmentHours)
            throw new McpInvalidArgumentsException($"hours must be between 1 and {MaxTreatmentHours}.");

        var clamped = httpContextAccessor.HttpContext?.IsCallerHistoryClamped() ?? false;
        var spec = Normalise(validator, TreatmentsSpec(hours, clamped), await renderer.UsesMmolAsync(cancellationToken));
        return (await renderer.RenderAsync(spec, cancellationToken)).Result;
    }

    [McpServerTool(Name = "compare_periods", Title = "Compare periods", ReadOnly = true, OpenWorld = false)]
    [McpAppUi(ResourceUri = ReportResources.ReportUri)]
    [Description(
        "Compares two periods side by side, laid out as the Nocturne comparison report: each period's time in each glucose "
        + "range, then each figure (GMI, CV, mean, time and episodes high and low, variability and risk indexes, data coverage, "
        + "and with treatments.read insulin and carbs) with both values and the change, marked better or worse where a lower "
        + "or higher value is better. Send 'preset', or the four dates. Returns the text of every figure.")]
    [McpRequireScope(Scope.GlucoseRead)]
    public static async Task<CallToolResult> ComparePeriods(
        ReportSpecValidator validator,
        ReportRenderer renderer,
        [Description("last7-prior7, last14-prior14, last30-prior30 or thisMonth-lastMonth. Default last14-prior14 when no dates are sent.")] string? preset = null,
        [Description("Period A's first day, yyyy-MM-dd. Send with aTo, bFrom and bTo, in place of preset.")] string? aFrom = null,
        [Description("Period A's last day, yyyy-MM-dd, counted.")] string? aTo = null,
        [Description("Period B's first day, yyyy-MM-dd.")] string? bFrom = null,
        [Description("Period B's last day, yyyy-MM-dd, counted.")] string? bTo = null,
        CancellationToken cancellationToken = default)
    {
        var validation = validator.Validate(
            JsonSerializer.SerializeToElement(ComparisonSpec(preset, aFrom, aTo, bFrom, bTo)), await renderer.UsesMmolAsync(cancellationToken));
        if (!validation.IsValid)
            throw new McpInvalidArgumentsException(
                "The periods are not valid. Change these and call compare_periods again:\n"
                + string.Join('\n', validation.Errors.Select(e => $"- {ComparisonArgument(e)}")));
        return (await renderer.RenderAsync(validation.Spec!, cancellationToken)).Result;
    }

    /// <summary>The comparison report as a spec: one comparison panel over the two periods the arguments name.</summary>
    internal static object ComparisonSpec(string? preset, string? aFrom, string? aTo, string? bFrom, string? bTo)
    {
        var dates = aFrom is not null || aTo is not null || bFrom is not null || bTo is not null;
        var dataset = new Dictionary<string, object?> { ["name"] = PeriodComparisonParameters.Name };
        if (preset is not null || !dates)
            dataset["preset"] = preset ?? "last14-prior14";
        if (dates)
        {
            if (aFrom is not null || aTo is not null)
                dataset["a"] = new { from = aFrom, to = aTo };
            if (bFrom is not null || bTo is not null)
                dataset["b"] = new { from = bFrom, to = bTo };
        }
        return new
        {
            version = ReportSpec.CurrentVersion,
            title = "Period comparison",
            panels = new object[]
            {
                new { id = "comparison", title = "Two periods side by side", dataset, primitive = "comparison" },
            },
        };
    }

    /// <summary>A spec error in the names of compare_periods' arguments.</summary>
    private static string ComparisonArgument(ReportSpecError error) => error.Path switch
    {
        "panels[0].dataset.preset" => $"preset: {error.Message}",
        "panels[0].dataset.a" or "panels[0].dataset.a.from" or "panels[0].dataset.a.to" => $"aFrom, aTo: {error.Message}",
        "panels[0].dataset.b" or "panels[0].dataset.b.from" or "panels[0].dataset.b.to" => $"bFrom, bTo: {error.Message}",
        _ => error.Message,
    };

    /// <summary>
    /// The web AGP report's layout as a spec: its figure strip, its 24-hour percentile chart (the hourly
    /// distribution it draws, <c>averagedStats</c>), and its time in range bar.
    /// </summary>
    /// <param name="clamped">The caller reads only the last 24 hours, so the title names the window the runner serves.</param>
    internal static object AgpSpec(int days, bool clamped = false) => new
    {
        version = ReportSpec.CurrentVersion,
        title = clamped
            ? "Ambulatory glucose profile, last 24 hours"
            : $"Ambulatory glucose profile, last {days} {(days == 1 ? "day" : "days")}",
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
    internal static object TreatmentsSpec(int hours, bool clamped = false) => new
    {
        version = ReportSpec.CurrentVersion,
        title = $"Treatments, last {Shown(hours, clamped)} {(Shown(hours, clamped) == 1 ? "hour" : "hours")}",
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

    /// <summary>The hours a clamped caller is served: at most the last 24.</summary>
    private static int Shown(int hours, bool clamped) => clamped ? Math.Min(hours, 24) : hours;

    /// <summary>A preset's spec as the validator normalises it; a preset that fails validation is a bug, not the caller's error.</summary>
    internal static ReportSpec Normalise(ReportSpecValidator validator, object spec, bool useMmol)
    {
        var validation = validator.Validate(JsonSerializer.SerializeToElement(spec), useMmol);
        return validation.IsValid
            ? validation.Spec!
            : throw new InvalidOperationException("A preset spec is not valid: " + string.Join("; ", validation.Errors));
    }
}
