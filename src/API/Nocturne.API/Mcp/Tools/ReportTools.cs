using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nocturne.API.Extensions;
using Nocturne.API.Mcp.Reports;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Nocturne.API.Mcp.Tools;

/// <summary><c>render_report</c>: the report spec, validated, run and drawn by the report view.</summary>
internal static class ReportTools
{
    public const string RenderReportName = "render_report";

    /// <summary>The tool, with <see cref="ReportSpecSchema.InputSchema"/> in place of the raw argument's.</summary>
    public static McpServerTool Create(IServiceProvider services)
    {
        var method = typeof(ReportTools).GetMethod(nameof(RenderReport), BindingFlags.Public | BindingFlags.Static)!;
        var tool = McpServerTool.Create(method, (object?)null, new McpServerToolCreateOptions { Services = services });
        tool.ProtocolTool.InputSchema = ReportSpecSchema.InputSchema;
        return tool;
    }

    [McpServerTool(Name = RenderReportName, Title = "Render report", ReadOnly = true, OpenWorld = false)]
    [McpAppUi(ResourceUri = ReportResources.ReportUri)]
    [Description(
        "Draws a report of one to six panels from Nocturne's data. Each panel reads one dataset over a "
        + "time range and draws it as one primitive: time_series (lines over time, with an optional shaded "
        + "band), stat_tiles (a row of figures), table, or glucose_chart (the full dashboard chart). "
        + "Datasets: glucose_series (readings with IOB, COB and basal), glucose_statistics (time in range, "
        + "mean, GMI, CV, SD). Call describe_dataset for a dataset's fields and limits. Returns a text summary "
        + "of each panel. An invalid spec is refused with a list of what to change.")]
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
        + "three made-up sample rows that show the shape of its rows. Reads no data.")]
    public static async Task<CallToolResult> DescribeDataset(
        IReportDatasetRegistry registry,
        ITenantAppearanceReader appearance,
        ITenantAccessor tenant,
        IOptions<MvcJsonOptions> jsonOptions,
        [Description("The dataset name: glucose_series or glucose_statistics.")] string name,
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
        var glucose = dataset.Fields.Where(f => f.Unit == ReportField.GlucoseUnit).Select(f => f.Name).ToHashSet();
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
