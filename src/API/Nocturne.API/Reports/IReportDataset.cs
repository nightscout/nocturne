using System.Text.Json.Serialization;
using Nocturne.API.Extensions;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Reports;

/// <summary>What a field holds, which decides where a mark may bind it and what the axis along it is.</summary>
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

    /// <summary>A local calendar day: its midnight, Unix milliseconds.</summary>
    [JsonStringEnumMemberName("day")]
    Day,

    /// <summary>A value of a scatter's first source, along its horizontal axis.</summary>
    [JsonStringEnumMemberName("value")]
    Value,

    /// <summary>Minutes one source is shifted against another.</summary>
    [JsonStringEnumMemberName("lag")]
    Lag,
}

/// <summary>How a field's values in one slot of a panel's grid fold into one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportAggregate>))]
internal enum ReportAggregate
{
    /// <summary>Amounts given in the slot: a bolus, carbs, an injection.</summary>
    [JsonStringEnumMemberName("sum")]
    Sum,

    /// <summary>A value that holds until the next one: a basal rate, a reservoir, a battery. Carried into a slot with none.</summary>
    [JsonStringEnumMemberName("last")]
    Last,

    /// <summary>Samples of a level: glucose, insulin on board.</summary>
    [JsonStringEnumMemberName("mean")]
    Mean,

    [JsonStringEnumMemberName("max")]
    Max,

    /// <summary>The lowest value in the slot: a range's low edge.</summary>
    [JsonStringEnumMemberName("min")]
    Min,
}

/// <summary>One field of a dataset's rows, as the catalogue and the result describe it.</summary>
/// <param name="Name">The key in each row.</param>
/// <param name="Label">The words the view prints for it.</param>
/// <param name="Kind">What it holds.</param>
/// <param name="Unit">
/// Its unit as printed, null for none. <c>glucose</c> stands for the tenant's display unit until
/// <see cref="ResolveUnit"/>, and so does a leading <c>glucose/</c> (<c>glucose/U</c> for an ISF).
/// </param>
/// <param name="Description">What it is.</param>
/// <param name="Scope">The read scope it needs beyond the dataset's own, or null.</param>
/// <param name="Color">The theme colour a line of it is drawn in.</param>
/// <param name="GlucoseScale">Drawn on the glucose scale and coloured by the glucose thresholds.</param>
/// <param name="Marker">The dashboard glyph a markers layer draws at each value.</param>
/// <param name="Step">The value holds until the next row, so a line of it is drawn as steps.</param>
/// <param name="Companion">
/// Sent with every row of the panel, bound or not: a row's sentence, or the cohort and range a band
/// row belongs to, which the view needs to colour and name what it draws.
/// </param>
/// <param name="ColorField">
/// The field each row carries its theme colour in, for a bar or a span of this category; the runner
/// sends it with the field, so the colour is the data's, never the view's.
/// </param>
/// <param name="Aggregate">How the field folds onto a panel's grid; null for a field that cannot be folded.</param>
/// <param name="Setting">
/// Why the field is not data a derived layer may take (<see cref="ReportSetting"/>): it is drawn as it is, and no
/// derived figure compares the data with the settings or rebuilds them.
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
    bool Companion = false,
    ReportAggregate? Aggregate = null,
    ReportSetting Setting = ReportSetting.None)
{
    public const string GlucoseUnit = "glucose";

    /// <summary>
    /// A row field holding one sentence the API wrote about the row; a <see cref="Companion"/> field.
    /// </summary>
    public const string SentenceField = "sentence";

    /// <summary>A row field that says, as <c>yes</c>, that the algorithm gave the row's bolus; a markers layer draws it outlined.</summary>
    public const string AutomaticField = "automatic";

    public bool IsNumber => Kind is ReportFieldKind.Number or ReportFieldKind.Percent;

    /// <summary>The horizontal axis a field of this kind lays a chart along, or null for a value field.</summary>
    public ReportXKind? AxisKind => Kind switch
    {
        ReportFieldKind.Time => ReportXKind.Time,
        ReportFieldKind.Offset => ReportXKind.Offset,
        ReportFieldKind.TimeOfDay => ReportXKind.TimeOfDay,
        ReportFieldKind.Day => ReportXKind.Day,
        ReportFieldKind.Category => ReportXKind.Category,
        ReportFieldKind.Value => ReportXKind.Value,
        ReportFieldKind.Lag => ReportXKind.Lag,
        _ => null,
    };

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

    /// <summary>The dataset's name in words, as the report builder lists it.</summary>
    string Label { get; }

    /// <summary>One line that says what the dataset holds.</summary>
    string Description { get; }

    /// <summary>The <see cref="ReportDatasetParameters"/> subtype its <c>dataset</c> object binds to.</summary>
    Type ParametersType { get; }

    /// <summary>The read scope without which the dataset is not read at all.</summary>
    string RequiredScope { get; }

    IReadOnlyList<ReportField> Fields { get; }

    /// <summary>The kinds of panel other than a chart that may show it, in the order the builder offers them.</summary>
    IReadOnlyList<ReportPanelKind> PanelKinds { get; }

    /// <summary>The marks a chart layer of it may be drawn as, the default first; none for a dataset no chart draws.</summary>
    IReadOnlyList<ReportMark> Marks { get; }

    /// <summary>The fields a panel of <paramref name="kind"/> may bind; a dataset whose rows differ by kind narrows them.</summary>
    IReadOnlyList<ReportField> FieldsFor(ReportPanelKind kind) => Fields;

    /// <summary>The bindings a layer drawn as <paramref name="mark"/> gets when it sends none, or null for the mark's own defaults.</summary>
    ReportLayerBindings? DefaultBindings(ReportMark mark) => null;

    /// <summary>The range a panel gets when neither it nor the report gives one.</summary>
    TimeSpan DefaultRange { get; }

    /// <summary>The longest range one read of this dataset may cover.</summary>
    TimeSpan MaxRange { get; }

    /// <summary>The groups the rows of a query come in (event groups); a derived source reads one.</summary>
    int SeriesCount(ReportDatasetParameters parameters) => 1;

    /// <summary>The row field holding a row's group number, for a dataset whose rows come in groups.</summary>
    string? SeriesField => null;

    /// <summary>The parameter that lists the groups, one item each, for a dataset with <see cref="SeriesField"/>.</summary>
    string? SeriesParameter => null;

    /// <summary>
    /// What a read of this dataset reads differently by, beyond its query, as a key part (<see cref="ReportReadShape"/>).
    /// Null for a dataset that reads the same rows for every shape, so its layers and sources share one read.
    /// </summary>
    string? ReadVariant(ReportReadShape shape) => null;

    /// <summary>
    /// The key of a costly read several datasets share, or null: reads with one shared key over one range
    /// compute once, and the read cap counts them once (<see cref="Datasets.EventResponseDataset.SharedReadKey"/>).
    /// </summary>
    string? SharedRead(ReportDatasetParameters parameters) => null;

    /// <summary>
    /// The fields the rows' sentence companion (<see cref="ReportField.SentenceField"/>) is about
    /// (<see cref="ReportAbout"/>); a chart layer carries it only when it draws them. Null for a sentence about the whole row.
    /// </summary>
    IReadOnlyList<string>? RowSentenceAbout => null;

    /// <summary>The rows of group <paramref name="series"/>, from 1, that a derived source reads.</summary>
    IEnumerable<IReadOnlyDictionary<string, object?>> SeriesRows(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, int series) => rows;

    /// <summary>The most reads of this dataset one report may make, for a dataset whose read costs more than one.</summary>
    int MaxPanelsPerReport => ReportSpec.MaxReads;

    /// <summary>The most rows one series of this dataset carries; longer ranges are downsampled.</summary>
    int PointBudget { get; }

    /// <summary>Errors in the parameters, as messages naming the parameter.</summary>
    IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters);

    /// <summary>Errors in a chart layer's bindings that only the dataset knows of, by the binding they name.</summary>
    IEnumerable<(string Field, string Message)> ValidateBindings(ReportLayerBindings bindings) => [];

    Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken);
}

/// <summary>One read of a dataset, for a panel or a layer.</summary>
/// <param name="Kind">The kind of panel the rows are for; a chart's layers and sources read as <see cref="ReportPanelKind.Chart"/>.</param>
/// <param name="Now">The instant the runner resolved <see cref="From"/> and <see cref="To"/> against; null outside a run.</param>
/// <param name="X">The field a chart layer lays along, for a dataset whose rows take the shape they are drawn in.</param>
internal sealed record ReportDatasetRequest(
    ReportDatasetParameters Parameters,
    ReportPanelKind Kind,
    DateTimeOffset From,
    DateTimeOffset To,
    bool UseMmol,
    IReadOnlySet<string> GrantedScopes,
    IServiceProvider Services,
    ReportReadLog ReadLog,
    bool HistoryClamped = false,
    Datasets.ReportRunCache? RunCache = null,
    DateTimeOffset? Now = null,
    string? X = null,
    TimeZoneInfo? Zone = null,
    IReadOnlyList<string>? Fields = null,
    bool Whole = false)
{
    public bool HasScope(string scope) => Scope.Satisfies(GrantedScopes, scope);

    /// <summary>The run's shared reads; a request made outside a run gets its own.</summary>
    public Datasets.ReportRunCache Cache { get; } = RunCache ?? new Datasets.ReportRunCache();
}

/// <summary>
/// How a chart layer or a derived source reads a dataset beyond its query: the bound fields a layer draws
/// (<see cref="ReportDatasetRequest.Fields"/>) and a derived source's read of every row
/// (<see cref="ReportDatasetRequest.Whole"/>). The runner keys its reads and the validator counts them by it.
/// </summary>
internal sealed record ReportReadShape(IReadOnlyList<string>? Fields = null, bool Whole = false)
{
    public static readonly ReportReadShape Panel = new();
}

/// <summary>Why a field is not data a derived layer may take as a source.</summary>
internal enum ReportSetting
{
    /// <summary>Data: a derived layer may take it.</summary>
    None,

    /// <summary>A value of the profile's settings: a scheduled basal, a profile ISF or carb ratio, a target.</summary>
    Profile,

    /// <summary>Worked out against the profile's scheduled basal, so a derived layer of it with the delivered basal rebuilds the schedule.</summary>
    ProfileRelative,

    /// <summary>A fixed limit the reports draw (the clinical consensus range), the same for everyone: not data.</summary>
    FixedLimit,
}

/// <summary>A dataset's rows over the whole catalogue, before the runner picks the bound fields.</summary>
internal sealed record ReportDatasetOutput
{
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }

    /// <summary>The panel's full text: every figure the panel read, in sentences.</summary>
    public required string Summary { get; init; }

    /// <summary>Fields left out because the token lacks their scope.</summary>
    public IReadOnlyList<ReportOmittedField> Omitted { get; init; } = [];

    public ReportResolution? Resolution { get; init; }

    /// <summary>The glucose cut-points in the display unit, for a dataset with glucose fields.</summary>
    public ReportThresholds? Thresholds { get; init; }

    /// <summary>The range the dataset read, where it differs from the one asked for: whole local days, say.</summary>
    public ReportWindow? Window { get; init; }

    /// <summary>Every cohort the panel asked for, in order, with its label, shown or not.</summary>
    public IReadOnlyList<ReportCohort> Cohorts { get; init; } = [];

    /// <summary>At most <see cref="ReportFacts.Max"/> figures the view prints under the panel, label above value.</summary>
    public IReadOnlyList<ReportFact> Facts { get; init; } = [];

    /// <summary>The one sentence the view prints under the facts; the summary carries every detail.</summary>
    public string? Sentence { get; init; }

    /// <summary>
    /// The fields <see cref="Sentence"/> is about (<see cref="ReportAbout"/>): a chart layer prints it only when it draws them all.
    /// Null for a sentence about the whole dataset; empty for one about a table's rows, which no chart prints.
    /// </summary>
    public IReadOnlyList<string>? SentenceAbout { get; init; }

    /// <summary>Sentences a chart layer prints in place of <see cref="Sentence"/> when it does not draw its fields, the first that fits.</summary>
    public IReadOnlyList<ReportSentence> OtherSentences { get; init; } = [];

    /// <summary>
    /// <see cref="Sentence"/> sets the data against the profile's settings: a layer that draws both prints it,
    /// a chart panel's headline never does.
    /// </summary>
    public bool SentenceComparesSettings { get; init; }

    /// <summary>
    /// Where the rows were cut to a budget or a read limit: the instant from which they hold every record,
    /// so a total is 0 in an empty slot only from there. Null when the rows hold every record of the range.
    /// </summary>
    public long? CompleteFrom { get; init; }

    /// <summary>The sentence a chart layer adds when the rows it draws were cut, saying so; null when they were not.</summary>
    public string? CutNote { get; init; }

    /// <summary>The panel's range and how much it read, as one line: "2026-10-15 08:39 to 2026-10-29 08:39 UTC. 4025 readings."</summary>
    public string? Context { get; init; }

    /// <summary>Labels that replace a field's catalogue label for this run: a comparison's period names, say.</summary>
    public IReadOnlyDictionary<string, string>? FieldLabels { get; init; }

    /// <summary>The hue a chart layer of these rows is drawn in, where the data names one: an event group's own hue.</summary>
    public ChartColor? Color { get; init; }

    /// <summary>
    /// The name a layer or panel of these rows gets when the spec gives none, where the rows are one group of
    /// something: one event group's label, the days a day filter kept, the weekdays read. Null for the default name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>How the days fell under a day filter, where one kept only some of them.</summary>
    public ReportDayCounts? Days { get; init; }

    /// <summary>The query the panel's web report opens with, in place of the range's days; null for the range's days.</summary>
    public IReadOnlyList<KeyValuePair<string, string?>>? LinkQuery { get; init; }
}

/// <summary>A sentence about <paramref name="About"/> (<see cref="ReportAbout"/>), the fields a chart layer must draw to print it.</summary>
internal sealed record ReportSentence(string Text, IReadOnlyList<string> About);

/// <summary>One shaded span of a band layer: its words ("Glucose P25–P75") and its low and high edge fields.</summary>
internal sealed record ReportBandSpan(string Label, string Low, string High)
{
    /// <summary>
    /// The spans of <paramref name="band"/> (two edges, or four: outer low, inner low, inner high, outer high), inner first.
    /// Two edges of one family ("Glucose P25", "Glucose P75") share its name once; edges of two families keep both names.
    /// </summary>
    public static IReadOnlyList<ReportBandSpan> Of(IReadOnlyList<ReportField> band)
    {
        static ReportBandSpan Span(ReportField low, ReportField high)
        {
            var a = low.Label.Split(' ');
            var b = high.Label.Split(' ');
            var label = a.Length == b.Length && a.Length > 1 && a[..^1].SequenceEqual(b[..^1])
                ? $"{string.Join(' ', a[..^1])} {a[^1]}–{b[^1]}"
                : $"{low.Label} to {high.Label}";
            return new ReportBandSpan(label, low.Name, high.Name);
        }
        return band.Count switch
        {
            4 => [Span(band[1], band[2]), Span(band[0], band[3])],
            2 => [Span(band[0], band[1])],
            _ => [],
        };
    }
}

/// <summary>An event cohort a panel asked for: its number (1 to 6, in the order sent) and the label that names its filter.</summary>
/// <param name="Omitted">Why the group is not shown, or null when it is.</param>
internal sealed record ReportCohort(
    int Number,
    string Label,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReportCohortOmission? Omitted = null);

/// <summary>Why an asked event group is not shown, as data.</summary>
/// <param name="Reason"><c>scope</c>: the token lacks <paramref name="Scope"/>; <c>sibling</c>: an all-others group whose group <paramref name="Group"/> is not shown.</param>
internal sealed record ReportCohortOmission(
    string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Group = null)
{
    public const string ScopeReason = "scope";
    public const string SiblingReason = "sibling";
}

/// <summary>
/// How the local days of a range fell under a day filter: the days kept, every day of the range, the days that had
/// what the filter names and those that did not, and the days in neither group (only partly in the range, or
/// starting before the history the token reads).
/// </summary>
internal sealed record ReportDayCounts(int Kept, int Total, int Matching, int NotMatching, int Partial, int Undecided)
{
    public static ReportDayCounts Of(Core.Models.LoopAnalysis.DaySelection selection) => new(
        selection.Kept.Count,
        selection.Matching + selection.NotMatching + selection.Partial + selection.Undecided,
        selection.Matching, selection.NotMatching, selection.Partial, selection.Undecided);
}

internal sealed record ReportOmittedField(string Field, string Scope);

/// <summary>A treatment marker on a markers layer: when, which, and its amount (units of insulin, grams of carbs, glucose).</summary>
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
