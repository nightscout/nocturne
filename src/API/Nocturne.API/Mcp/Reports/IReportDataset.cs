using System.Text.Json.Serialization;
using Nocturne.API.Extensions;
using Nocturne.Core.Constants;
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

    /// <summary>Minutes from an event's anchor, negative before it.</summary>
    [JsonStringEnumMemberName("offset")]
    Offset,

    /// <summary>Minutes after local midnight.</summary>
    [JsonStringEnumMemberName("time_of_day")]
    TimeOfDay,
}

/// <summary>One field of a dataset's rows, as the catalogue and the result describe it.</summary>
/// <param name="Name">The key in each row.</param>
/// <param name="Label">The words the view prints for it.</param>
/// <param name="Kind">What it holds.</param>
/// <param name="Unit">
/// Its unit as printed, null for none. <c>glucose</c> stands for the tenant's display unit until
/// <see cref="ResolveUnit"/>, and so does a leading <c>glucose/</c> (<c>glucose/U</c> for an ISF).
/// </param>
/// <param name="Description">What it is, for the model.</param>
/// <param name="Scope">The read scope it needs beyond the dataset's own, or null.</param>
/// <param name="Color">The theme colour a line of it is drawn in.</param>
/// <param name="GlucoseScale">Drawn on the glucose scale and coloured by the glucose thresholds.</param>
/// <param name="Marker">Drawn on a time series as the dashboard's bolus or carb marker at each value, not as a line.</param>
/// <param name="Step">The value holds until the next row, so a line of it is drawn as steps.</param>
/// <param name="Companion">
/// Sent with every row of the panel, bound or not: a row's sentence, or the cohort and range a band
/// row belongs to, which the view needs to colour and name what it draws.
/// </param>
/// <param name="ColorField">
/// The field each row carries its theme colour in, for a bar of this category; the runner sends it
/// with the field, so a bar's colour is the data's, never the view's.
/// </param>
internal sealed record ReportField(
    string Name,
    string Label,
    ReportFieldKind Kind,
    string? Unit,
    string Description,
    string? Scope = null,
    ChartColor? Color = null,
    bool GlucoseScale = false,
    ReportMarkerKind? Marker = null,
    bool Step = false,
    string? ColorField = null,
    bool Companion = false)
{
    public const string GlucoseUnit = "glucose";

    /// <summary>
    /// A row field holding one sentence the API wrote about the row, which a chart hands the assistant
    /// when the reader picks the row; a <see cref="Companion"/> field.
    /// </summary>
    public const string SentenceField = "sentence";

    /// <summary>Whether the field's values are glucose, or glucose per something, so written in the display unit.</summary>
    public bool InGlucoseUnit => Unit is not null && (Unit == GlucoseUnit || Unit.StartsWith(GlucoseUnit + "/", StringComparison.Ordinal));

    public ReportField ResolveUnit(bool useMmol) =>
        InGlucoseUnit ? this with { Unit = (useMmol ? "mmol/L" : "mg/dL") + Unit![GlucoseUnit.Length..] } : this;
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

    /// <summary>The fields a panel drawn as <paramref name="primitive"/> may bind; a dataset whose rows differ by primitive narrows them.</summary>
    IReadOnlyList<ReportField> FieldsFor(ReportPrimitive primitive) => Fields;

    /// <summary>The bindings a panel drawn as <paramref name="primitive"/> gets when it sends none, or null for the primitive's own defaults.</summary>
    ReportBindings? DefaultBindings(ReportPrimitive primitive) => null;

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

    /// <summary>Errors in parameters that depend on how the panel draws the dataset and over how long.</summary>
    IEnumerable<(string Field, string Message)> ValidatePanel(ReportDatasetParameters parameters, ReportPrimitive primitive, TimeSpan span) => [];

    Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken);
}

/// <summary>One panel's run of a dataset.</summary>
/// <param name="Now">The instant the runner resolved <see cref="From"/> and <see cref="To"/> against; null outside a run.</param>
/// <param name="Bindings">The panel's normalised bindings, for a dataset whose rows take the shape they bind.</param>
internal sealed record ReportDatasetRequest(
    ReportDatasetParameters Parameters,
    ReportPrimitive Primitive,
    DateTimeOffset From,
    DateTimeOffset To,
    bool UseMmol,
    IReadOnlySet<string> GrantedScopes,
    IServiceProvider Services,
    McpToolReadLog ReadLog,
    bool HistoryClamped = false,
    Datasets.ReportRunCache? RunCache = null,
    DateTimeOffset? Now = null,
    ReportBindings? Bindings = null)
{
    public bool HasScope(string scope) => Scope.Satisfies(GrantedScopes, scope);

    /// <summary>The run's shared reads; a request made outside a run gets its own.</summary>
    public Datasets.ReportRunCache Cache { get; } = RunCache ?? new Datasets.ReportRunCache();
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

    /// <summary>The range the dataset read, where it differs from the one asked for: whole local days, say.</summary>
    public ReportWindow? Window { get; init; }

    /// <summary>Treatment markers to draw on a time series panel.</summary>
    public IReadOnlyList<ReportMarker> Markers { get; init; } = [];

    /// <summary>Every cohort the panel asked for, in order, with its label, shown or not.</summary>
    public IReadOnlyList<ReportCohort> Cohorts { get; init; } = [];

    /// <summary>Single events' glucose aligned on their anchor, which a glucose chart of cohorts draws under the band.</summary>
    public IReadOnlyList<ReportTrace> Traces { get; init; } = [];
}

/// <summary>
/// One event's glucose over the window, aligned on its anchor.
/// </summary>
/// <param name="Cohort">The cohort's number, as <see cref="ReportCohort.Number"/>.</param>
/// <param name="At">The anchor, Unix milliseconds.</param>
/// <param name="Points">[minutes from the anchor, glucose in the display unit] pairs; slots with no reading are left out.</param>
/// <param name="Sentence">What the event's figures show, as the event table's sentence.</param>
internal sealed record ReportTrace(int Cohort, long At, IReadOnlyList<double[]> Points, string Sentence);

/// <summary>An event cohort a panel asked for: its number (1 to 3, in the order sent) and the label that names its filter.</summary>
internal sealed record ReportCohort(int Number, string Label);

internal sealed record ReportOmittedField(string Field, string Scope);

/// <summary>A treatment marker on a time series: when, which, and its amount (units of insulin, grams of carbs).</summary>
/// <param name="Automatic">A bolus the algorithm gave, drawn outlined as the dashboard draws it.</param>
internal sealed record ReportMarker(long Time, ReportMarkerKind Kind, double Amount, bool Automatic = false);

/// <summary>How the rows were spaced: the interval asked for and the one the point budget allowed.</summary>
internal sealed record ReportResolution(
    int RequestedIntervalMinutes,
    int IntervalMinutes,
    int SourceRows,
    int Rows,
    bool Downsampled);

/// <summary>Glucose cut-points in the display unit, and the mg/dL in one display unit (1, or 18.0182 for mmol/L).</summary>
/// <remarks>The view needs the factor to place the continuous glucose spectrum, whose stops are in mg/dL, on a display-unit axis.</remarks>
internal sealed record ReportThresholds(double VeryLow, double Low, double High, double VeryHigh, double MgdlPerUnit)
{
    /// <summary><paramref name="thresholds"/> (mg/dL) in the display unit; the clinical consensus limits by default.</summary>
    public static ReportThresholds Of(GlycemicThresholds? thresholds, bool useMmol)
    {
        var t = thresholds ?? new GlycemicThresholds();
        return new ReportThresholds(
            GlucoseUnits.Scale(t.VeryLow, useMmol),
            GlucoseUnits.Scale(t.Low, useMmol),
            GlucoseUnits.Scale(t.High, useMmol),
            GlucoseUnits.Scale(t.VeryHigh, useMmol),
            useMmol ? GlucoseConstants.MgdlPerMmol : 1);
    }
}

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
