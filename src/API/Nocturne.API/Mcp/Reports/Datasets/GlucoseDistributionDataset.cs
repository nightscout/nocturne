using System.Globalization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Mcp.Reports.Datasets;

/// <summary>
/// The range's time in each clinical glucose band, from the analysis <c>glucose_statistics</c> reads
/// (<see cref="GlucoseStatisticsDataset.AnalyseAsync"/>): the split the AGP report's time in range
/// bar draws. Drawn as a bar, one row per band, each row carrying its band's theme colour; as stat
/// tiles, one row with a field per band.
/// </summary>
internal sealed class GlucoseDistributionDataset : IReportDataset
{
    public string Name => GlucoseDistributionParameters.Name;

    public string Description =>
        "Glucose distribution: the percent of the range's covered time in each clinical glucose band (very low, low, "
        + "in range, high, very high) and the minutes in each, from the same analysis as glucose_statistics. Draw as "
        + "bar (one bar per band, in the band's color) or stat_tiles (one tile per band).";

    public Type ParametersType => typeof(GlucoseDistributionParameters);

    public string RequiredScope => Scope.GlucoseRead;

    private static readonly IReadOnlyList<ReportField> BarFields =
    [
        new("band", "Band", ReportFieldKind.Category, null, "The glucose band.", ColorField: "color"),
        new("range", "Range", ReportFieldKind.Category, null, "The band's limits, in the display unit."),
        new("percent", "Time", ReportFieldKind.Percent, "%", "Percent of the covered time in the band."),
        new("minutes", "Minutes", ReportFieldKind.Number, "min", "Minutes in the band over the range."),
        new("color", "Color", ReportFieldKind.Category, null, "The band's theme color. Its bar is drawn in this color."),
    ];

    private static readonly IReadOnlyList<ReportField> TileFields =
    [
        new("veryLow", "Very low", ReportFieldKind.Percent, "%", "Percent of the covered time very low.", Color: ChartColor.GlucoseVeryLow),
        new("low", "Low", ReportFieldKind.Percent, "%", "Percent of the covered time low.", Color: ChartColor.GlucoseLow),
        new("inRange", "In range", ReportFieldKind.Percent, "%", "Percent of the covered time in range.", Color: ChartColor.GlucoseInRange),
        new("high", "High", ReportFieldKind.Percent, "%", "Percent of the covered time high.", Color: ChartColor.GlucoseHigh),
        new("veryHigh", "Very high", ReportFieldKind.Percent, "%", "Percent of the covered time very high.", Color: ChartColor.GlucoseVeryHigh),
    ];

    public IReadOnlyList<ReportField> Fields { get; } = [.. BarFields, .. TileFields];

    public IReadOnlyList<ReportField> FieldsFor(ReportPrimitive primitive) => primitive == ReportPrimitive.Bar ? BarFields : TileFields;

    public ReportBindings? DefaultBindings(ReportPrimitive primitive) => primitive == ReportPrimitive.Bar
        ? new ReportBindings { X = "band", Y = ["percent"], Stacked = false }
        : null;

    public IReadOnlyList<ReportPrimitive> AllowedPrimitives { get; } = [ReportPrimitive.Bar, ReportPrimitive.StatTiles];

    public TimeSpan DefaultRange => TimeSpan.FromDays(14);

    public TimeSpan MaxRange(ReportPrimitive primitive) => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => 5;

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> SampleRows { get; } =
    [
        new Dictionary<string, object?>
        {
            ["band"] = "In range", ["range"] = "70-180 mg/dL", ["percent"] = 71.8, ["minutes"] = 14476.0, ["color"] = ChartColor.GlucoseInRange,
        },
        new Dictionary<string, object?>
        {
            ["veryLow"] = 0.4, ["low"] = 1.6, ["inRange"] = 71.8, ["high"] = 20.9, ["veryHigh"] = 5.3,
        },
    ];

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    /// <summary>One clinical band: its key, its words, its limits in mg/dL (null for open) and its colour.</summary>
    internal sealed record Band(string Key, string Label, double? Low, double? High, ChartColor Color, double Percent, double Minutes);

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var (_, analysis) = await GlucoseStatisticsDataset.AnalyseAsync(request, cancellationToken);
        var count = analysis.BasicStats.Count;
        var bands = Bands(analysis.TimeInRange);
        var useMmol = request.UseMmol;

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = count == 0 ? []
            : request.Primitive == ReportPrimitive.Bar
                ? [.. bands.Select(b => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["band"] = b.Label,
                    ["range"] = Limits(b, useMmol),
                    ["percent"] = Math.Round(b.Percent, 1),
                    ["minutes"] = Math.Round(b.Minutes),
                    ["color"] = b.Color,
                })]
                : [bands.ToDictionary(b => b.Key, b => (object?)Math.Round(b.Percent, 1))];

        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = Summarise(bands, count, request.From, request.To, useMmol),
        };
    }

    /// <summary>The five bands, lowest first, with the analysis's limits and time.</summary>
    internal static IReadOnlyList<Band> Bands(TimeInRangeMetrics tir)
    {
        var t = tir.Thresholds ?? new GlycemicThresholds();
        var p = tir.Percentages;
        var d = tir.Durations;
        return
        [
            new("veryLow", "Very low", null, t.VeryLow, ChartColor.GlucoseVeryLow, p.VeryLow, d.VeryLow),
            new("low", "Low", t.VeryLow, t.Low, ChartColor.GlucoseLow, p.Low, d.Low),
            new("inRange", "In range", t.TargetBottom, t.TargetTop, ChartColor.GlucoseInRange, p.Target, d.Target),
            new("high", "High", t.High, t.VeryHigh, ChartColor.GlucoseHigh, p.High, d.High),
            new("veryHigh", "Very high", t.VeryHigh, null, ChartColor.GlucoseVeryHigh, p.VeryHigh, d.VeryHigh),
        ];
    }

    private static string Limits(Band band, bool useMmol)
    {
        var unit = useMmol ? "mmol/L" : "mg/dL";
        string G(double mgdl) => GlucoseSummary.Format(mgdl, useMmol);
        return (band.Low, band.High) switch
        {
            (null, { } high) => $"below {G(high)} {unit}",
            ({ } low, null) => $"above {G(low)} {unit}",
            ({ } low, { } high) => $"{G(low)}-{G(high)} {unit}",
            _ => "",
        };
    }

    internal static string Summarise(IReadOnlyList<Band> bands, int readings, DateTimeOffset from, DateTimeOffset to, bool useMmol)
    {
        var head = string.Create(CultureInfo.InvariantCulture,
            $"Glucose distribution from {from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm} UTC.");
        if (readings == 0)
            return head + "\nNo glucose readings in this period.";

        var parts = bands.Select(b =>
            $"{b.Label.ToLowerInvariant()} ({Limits(b, useMmol)}) {b.Percent.ToString("0.#", CultureInfo.InvariantCulture)}%");
        return string.Join('\n',
            head,
            string.Create(CultureInfo.InvariantCulture, $"Readings: {readings}."),
            $"Time in each band: {string.Join(", ", parts)}.");
    }
}
