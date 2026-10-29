using System.Text.Json.Serialization;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Mcp.Reports;

/// <summary>What a field holds, which decides where a primitive may bind it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportFieldKind>))]
internal enum ReportFieldKind
{
    [JsonStringEnumMemberName("time")]
    Time,

    [JsonStringEnumMemberName("number")]
    Number,

    [JsonStringEnumMemberName("category")]
    Category,

    [JsonStringEnumMemberName("percent")]
    Percent,
}

/// <summary>One field of a dataset's rows, as the catalogue and the result describe it.</summary>
/// <param name="Name">The key in each row.</param>
/// <param name="Label">The words the view prints for it.</param>
/// <param name="Kind">What it holds.</param>
/// <param name="Unit">Its unit as printed, null for none. <c>glucose</c> stands for the tenant's display unit until <see cref="ResolveUnit"/>.</param>
/// <param name="Description">What it is, for the model.</param>
/// <param name="Scope">The read scope it needs beyond the dataset's own, or null.</param>
/// <param name="Color">The theme colour a line of it is drawn in.</param>
/// <param name="GlucoseScale">Drawn on the glucose scale and coloured by the glucose thresholds.</param>
internal sealed record ReportField(
    string Name,
    string Label,
    ReportFieldKind Kind,
    string? Unit,
    string Description,
    string? Scope = null,
    ChartColor? Color = null,
    bool GlucoseScale = false)
{
    public const string GlucoseUnit = "glucose";

    public ReportField ResolveUnit(bool useMmol) =>
        Unit == GlucoseUnit ? this with { Unit = useMmol ? "mmol/L" : "mg/dL" } : this;
}

/// <summary>
/// A dataset the report spec may name. Datasets are stateless singletons; a run gets the
/// request's services through <see cref="ReportDatasetRequest"/>, so it reads under the request's
/// tenant, credential and read context.
/// </summary>
internal interface IReportDataset
{
    string Name { get; }

    string Description { get; }

    /// <summary>The <see cref="ReportDatasetParameters"/> subtype its <c>dataset</c> object binds to.</summary>
    Type ParametersType { get; }

    /// <summary>The read scope without which the dataset is not read at all.</summary>
    string RequiredScope { get; }

    IReadOnlyList<ReportField> Fields { get; }

    IReadOnlyList<ReportPrimitive> AllowedPrimitives { get; }

    /// <summary>The range a panel gets when neither it nor the report gives one.</summary>
    TimeSpan DefaultRange { get; }

    /// <summary>The longest range a panel of this dataset drawn as <paramref name="primitive"/> may ask for.</summary>
    TimeSpan MaxRange(ReportPrimitive primitive);

    /// <summary>The most rows one series of this dataset carries; longer ranges are downsampled.</summary>
    int PointBudget { get; }

    /// <summary>Rows that show the shape of the output. Made up, not read.</summary>
    IReadOnlyList<IReadOnlyDictionary<string, object?>> SampleRows { get; }

    /// <summary>Errors in the parameters, as messages naming the parameter.</summary>
    IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters);

    Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken);
}

/// <summary>One panel's run of a dataset.</summary>
internal sealed record ReportDatasetRequest(
    ReportDatasetParameters Parameters,
    ReportPrimitive Primitive,
    DateTimeOffset From,
    DateTimeOffset To,
    bool UseMmol,
    IReadOnlySet<string> GrantedScopes,
    IServiceProvider Services,
    McpToolReadLog ReadLog)
{
    public bool HasScope(string scope) => Scope.Satisfies(GrantedScopes, scope);
}

/// <summary>A dataset's rows over the whole catalogue, before the runner picks the bound fields.</summary>
internal sealed record ReportDatasetOutput
{
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }

    /// <summary>The text an assistant reads for the panel.</summary>
    public required string Summary { get; init; }

    /// <summary>Fields left out because the token lacks their scope.</summary>
    public IReadOnlyList<ReportOmittedField> Omitted { get; init; } = [];

    public ReportResolution? Resolution { get; init; }

    /// <summary>The glucose cut-points in the display unit, for a dataset with glucose fields.</summary>
    public ReportThresholds? Thresholds { get; init; }

    /// <summary>The dashboard chart payload, redacted, for <see cref="ReportPrimitive.GlucoseChart"/>.</summary>
    public DashboardChartData? Chart { get; init; }

    /// <summary>The read scopes the token lacks, for the chart's legend.</summary>
    public IReadOnlyList<string> OmittedScopes { get; init; } = [];
}

internal sealed record ReportOmittedField(string Field, string Scope);

/// <summary>How the rows were spaced: the interval asked for and the one the point budget allowed.</summary>
internal sealed record ReportResolution(
    int RequestedIntervalMinutes,
    int IntervalMinutes,
    int SourceRows,
    int Rows,
    bool Downsampled);

/// <summary>Glucose cut-points in the display unit, and the mg/dL in one display unit (1, or 18.0182 for mmol/L).</summary>
/// <remarks>The view needs the factor to place the continuous glucose spectrum, whose stops are in mg/dL, on a display-unit axis.</remarks>
internal sealed record ReportThresholds(double VeryLow, double Low, double High, double VeryHigh, double MgdlPerUnit);

/// <summary>The registered datasets, by name.</summary>
internal interface IReportDatasetRegistry
{
    IReadOnlyList<IReportDataset> All { get; }

    IReportDataset? Find(string name);
}

internal sealed class ReportDatasetRegistry(IEnumerable<IReportDataset> datasets) : IReportDatasetRegistry
{
    private readonly IReadOnlyList<IReportDataset> _all = [.. datasets.OrderBy(d => d.Name, StringComparer.Ordinal)];

    public IReadOnlyList<IReportDataset> All => _all;

    public IReportDataset? Find(string name) => _all.FirstOrDefault(d => d.Name == name);
}
