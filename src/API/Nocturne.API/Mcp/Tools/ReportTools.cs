using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nocturne.API.Extensions;
using Nocturne.API.Mcp.Images;
using Nocturne.API.Mcp.Reports;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Nocturne.API.Mcp.Tools;

/// <summary><c>render_report</c>: the report spec, validated, run and drawn by the report view.</summary>
internal static class ReportTools
{
    public const string RenderReportName = "render_report";

    public const string RefineReportName = "refine_report";

    public const string RenderReportImageName = "render_report_image";

    /// <summary>When the image tool is the one to use, said the same way in every description that offers it.</summary>
    public const string WhenToUseImages =
        "Use it for a document or message that cannot show the interactive view, such as a written report, a doc or an email.";

    /// <summary>The tool, with <see cref="ReportSpecSchema.InputSchema"/> in place of the raw argument's.</summary>
    public static McpServerTool Create(IServiceProvider services)
    {
        var method = typeof(ReportTools).GetMethod(nameof(RenderReport), BindingFlags.Public | BindingFlags.Static)!;
        var tool = McpServerTool.Create(method, (object?)null, new McpServerToolCreateOptions { Services = services });
        tool.ProtocolTool.InputSchema = ReportSpecSchema.InputSchema;
        return tool;
    }

    /// <summary><c>refine_report</c>, with <see cref="ReportSpecSchema.RefineInputSchema"/> in place of the raw arguments'.</summary>
    public static McpServerTool CreateRefine(IServiceProvider services)
    {
        var method = typeof(ReportTools).GetMethod(nameof(RefineReport), BindingFlags.Public | BindingFlags.Static)!;
        var tool = McpServerTool.Create(method, (object?)null, new McpServerToolCreateOptions { Services = services });
        tool.ProtocolTool.InputSchema = ReportSpecSchema.RefineInputSchema;
        return tool;
    }

    /// <summary><c>render_report_image</c>, with <see cref="ReportSpecSchema.ImageInputSchema"/> in place of the raw arguments'.</summary>
    public static McpServerTool CreateImage(IServiceProvider services)
    {
        var method = typeof(ReportTools).GetMethod(nameof(RenderReportImage), BindingFlags.Public | BindingFlags.Static)!;
        var tool = McpServerTool.Create(method, (object?)null, new McpServerToolCreateOptions { Services = services });
        tool.ProtocolTool.InputSchema = ReportSpecSchema.ImageInputSchema;
        return tool;
    }

    [McpServerTool(Name = RenderReportImageName, Title = "Render report image", ReadOnly = true, OpenWorld = false)]
    [McpAppUi(Visibility = [McpUiToolVisibility.Model])]
    [Description(
        "Draws the chart panels of a report spec as PNG images, with the same charts and clinical colours as the report "
        + "view: the same spec, datasets, primitives and limits as render_report. " + WhenToUseImages + " Returns each "
        + "panel's text summary, then one image per chart panel (time_series, range_bands, bar, comparison, glucose_chart; "
        + "at most 4, or the one panelId names). Stat tiles and tables have no image: their figures are in the text. The "
        + "images together may be at most about 1.5 MB; a larger answer is refused with what to lower.")]
    public static async Task<CallToolResult> RenderReportImage(
        ReportSpecValidator validator,
        ReportRenderer renderer,
        ReportImageRenderer images,
        [Description(ReportSpecSchema.SpecDescription)] JsonElement? spec = null,
        [Description(ReportSpecSchema.PanelIdDescription)] string? panelId = null,
        [Description(ReportSpecSchema.WidthDescription)] int? width = null,
        [Description(ReportSpecSchema.HeightDescription)] int? height = null,
        [Description(ReportSpecSchema.ThemeDescription)] string? theme = null,
        [Description(ReportSpecSchema.PixelRatioDescription)] int? devicePixelRatio = null,
        CancellationToken cancellationToken = default)
    {
        var options = ReportImageRenderer.ReadOptions(RenderReportImageName, panelId, width, height, theme, devicePixelRatio);
        var validation = validator.Validate(spec ?? default, await renderer.UsesMmolAsync(cancellationToken));
        if (!validation.IsValid)
            throw new McpInvalidArgumentsException(
                "The report spec is not valid. Change these and call render_report_image again:\n"
                + string.Join('\n', validation.Errors.Select(e => $"- {e}")));
        ReportImageRenderer.CheckPanels(validation.Spec!, options, RenderReportImageName);

        return await images.RenderAsync(validation.Spec!, options, cancellationToken);
    }

    [McpServerTool(Name = RenderReportName, Title = "Render report", ReadOnly = true, OpenWorld = false)]
    [McpAppUi(ResourceUri = ReportResources.ReportUri)]
    [Description(
        "Draws a report of one to six panels from Nocturne's data. Each panel reads one dataset over a "
        + "time range and draws it as one primitive: time_series (lines over time, with an optional shaded "
        + "band), stat_tiles (a row of figures), table, glucose_chart (the full dashboard chart; over event_response, single events under each cohort's band), or "
        + "range_bands (percentile bands under a median line, over the time of day or minutes from an event), or bar "
        + "(bars over a category, side by side or stacked). "
        + "Datasets: glucose_series (readings with IOB, COB and basal, and bolus and carb markers), glucose_statistics "
        + "(time in range, mean, GMI, CV, SD, insulin and carbs per day), glucose_distribution (time in each glucose band), "
        + "daily_summary (one row per day), treatments (bolus, carb, temp basal, note, override and injection records), "
        + "basal_delivery (delivered beside scheduled basal), device_status (reservoir and batteries), event_response "
        + "(meals and other events aligned on their start, by cohort, with a food and weekday filter; ids from find_foods), "
        + "settings_evidence (observed basal, ISF and carb ratio beside the profile's), daily_profile (each five-minute "
        + "slot of the day), glucose_distribution_by_hour. "
        + "Call describe_dataset for a dataset's fields and limits. Returns a text summary "
        + "of each panel. An invalid spec is refused with a list of what to change. For the same report as PNG images, "
        + "call render_report_image. " + WhenToUseImages)]
    public static async Task<CallToolResult> RenderReport(
        ReportSpecValidator validator,
        ReportRenderer renderer,
        [Description(ReportSpecSchema.SpecDescription)] JsonElement? spec = null,
        CancellationToken cancellationToken = default)
    {
        var validation = validator.Validate(spec ?? default, await renderer.UsesMmolAsync(cancellationToken));
        if (!validation.IsValid)
            throw new McpInvalidArgumentsException(
                "The report spec is not valid. Change these and call render_report again:\n"
                + string.Join('\n', validation.Errors.Select(e => $"- {e}")));

        return (await renderer.RenderAsync(validation.Spec!, cancellationToken)).Result;
    }

    [McpServerTool(Name = RefineReportName, Title = "Refine report", ReadOnly = true, OpenWorld = false)]
    [McpAppUi(ResourceUri = ReportResources.ReportUri, Visibility = [McpUiToolVisibility.App])]
    [Description(
        "For the report view: draws a report again with a patch applied to the spec render_report returned: a new range "
        + "for every panel, or a panel's new range or dataset parameters. Validated and limited as render_report is, except "
        + "that a range longer than a panel's dataset covers is cut to the latest part it covers, and the panel's summary says so.")]
    public static async Task<CallToolResult> RefineReport(
        ReportSpecValidator validator,
        ReportRenderer renderer,
        [Description("The normalised spec render_report returned.")] JsonElement? spec = null,
        [Description(ReportSpecSchema.PatchDescription)] JsonElement? patch = null,
        CancellationToken cancellationToken = default)
    {
        var (patched, errors) = ReportSpecPatches.Apply(spec ?? default, patch ?? default);
        if (patched is not { } merged)
            throw new McpInvalidArgumentsException(
                "The patch cannot be applied. Change these and call refine_report again:\n"
                + string.Join('\n', errors.Select(e => $"- {e}")));

        var validation = validator.Validate(merged, await renderer.UsesMmolAsync(cancellationToken), clampRanges: true);
        if (!validation.IsValid)
            throw new McpInvalidArgumentsException(
                "The refined report spec is not valid. Change these and call refine_report again:\n"
                + string.Join('\n', validation.Errors.Select(e => $"- {e}")));

        return (await renderer.RenderAsync(validation.Spec!, cancellationToken, validation.Shortened)).Result;
    }
}

/// <summary><c>describe_dataset</c>: one dataset's catalogue entry, for the model and the view.</summary>
[McpServerToolType]
internal sealed class DatasetTools
{
    [McpServerTool(Name = "describe_dataset", Title = "Describe dataset", ReadOnly = true, OpenWorld = false, Idempotent = true)]
    [McpAppUi(Visibility = [McpUiToolVisibility.Model, McpUiToolVisibility.App])]
    [Description(
        "Describes a dataset render_report can draw: what it holds, its parameters, its fields with units "
        + "and kinds, the primitives that can draw it, its default and longest range, its point limit, and "
        + "three made-up sample rows that show the shape of its rows. Reads no data. A panel drawn as a chart "
        + "primitive can also be drawn as an image by render_report_image. " + ReportTools.WhenToUseImages)]
    public static async Task<CallToolResult> DescribeDataset(
        IReportDatasetRegistry registry,
        ITenantAppearanceReader appearance,
        ITenantAccessor tenant,
        IOptions<MvcJsonOptions> jsonOptions,
        [Description("The dataset name, one of those render_report lists.")] string name,
        CancellationToken cancellationToken = default)
    {
        var dataset = registry.Find(name)
            ?? throw new McpInvalidArgumentsException(
                $"'{name}' is not a dataset. Send one of: {string.Join(", ", registry.All.Select(d => d.Name))}.");

        var useMmol = await GlucoseUnits.UsesMmolAsync(appearance, tenant, cancellationToken);
        var description = Describe(dataset, useMmol);
        var json = JsonSerializer.SerializeToElement(description, jsonOptions.Value.JsonSerializerOptions);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(json, IndentedJson) }],
            StructuredContent = json,
        };
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    internal static DatasetDescription Describe(IReportDataset dataset, bool useMmol) => new(
        dataset.Name,
        dataset.Description,
        ReportSpecSchema.ParametersSchema(dataset.ParametersType),
        dataset.RequiredScope,
        [.. dataset.Fields.Select(f => f.ResolveUnit(useMmol))],
        [.. dataset.AllowedPrimitives.Select(p => new DatasetPrimitive(p, dataset.MaxRange(p).TotalHours))],
        dataset.DefaultRange.TotalHours,
        dataset.PointBudget,
        SampleRows(dataset, useMmol));

    /// <summary>The sample rows, written in mg/dL, in the tenant's unit.</summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> SampleRows(IReportDataset dataset, bool useMmol)
    {
        var glucose = dataset.Fields.Where(f => f.InGlucoseUnit).Select(f => f.Name).ToHashSet();
        return [.. dataset.SampleRows.Select(row => (IReadOnlyDictionary<string, object?>)row.ToDictionary(
            kv => kv.Key,
            kv => glucose.Contains(kv.Key) && kv.Value is double mgdl ? GlucoseUnits.Scale(mgdl, useMmol) : kv.Value))];
    }
}

internal sealed record DatasetDescription(
    string Name,
    string Description,
    JsonElement Parameters,
    string RequiredScope,
    IReadOnlyList<ReportField> Fields,
    IReadOnlyList<DatasetPrimitive> Primitives,
    double DefaultRangeHours,
    int PointBudget,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> SampleRows);

internal sealed record DatasetPrimitive(ReportPrimitive Primitive, double MaxRangeHours);
