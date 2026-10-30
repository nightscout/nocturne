using System.Globalization;
using System.Text;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Mcp.Reports.Datasets;

/// <summary>
/// The treatment records in the range, one row each, read from the repositories the Treatment Log
/// reads (<c>TreatmentLogController</c>) and, for temp basals and overrides, the ones the dashboard
/// chart draws them from. Every kind is a treatments.read category.
/// </summary>
internal sealed class TreatmentsDataset : IReportDataset
{
    /// <summary>The most rows a panel carries; a range with more keeps its latest and says so.</summary>
    public const int Budget = 500;

    /// <summary>The most records read per kind, as the Treatment Log reads.</summary>
    internal const int RecordLimit = 10_000;

    internal const int MaxDetailLength = 120;

    public string Name => TreatmentsParameters.Name;

    public string Description =>
        "Treatment records in the range, one row each, oldest first: boluses (with the algorithm's), carb entries, temp "
        + "basals, notes, overrides and long-acting injections. Filter by 'types', 'minCarbs' and 'minInsulin'. Draw as table, "
        + "or time_series with y 'insulin' or 'carbs', which draws each record as the dashboard's bolus or carb marker. At most "
        + $"{Budget} rows: a range with more keeps the latest {Budget}.";

    public Type ParametersType => typeof(TreatmentsParameters);

    public string RequiredScope => Scope.TreatmentsRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("time", "Time", ReportFieldKind.Time, null, "When the record starts, Unix milliseconds."),
        new("type", "Type", ReportFieldKind.Category, null, "bolus, carbs, temp_basal, note, override or basal_injection."),
        new("insulin", "Insulin", ReportFieldKind.Number, "U", "Units of a bolus or a long-acting injection.",
            Color: ChartColor.InsulinBolus, Marker: ReportMarkerKind.Bolus),
        new("carbs", "Carbs", ReportFieldKind.Number, "g", "Grams of a carb entry.", Color: ChartColor.Carbs, Marker: ReportMarkerKind.Carbs),
        new("rate", "Rate", ReportFieldKind.Number, "U/h", "The rate of a temp basal."),
        new("durationMinutes", "Duration", ReportFieldKind.Number, "min", "How long a temp basal or override ran. Empty while it runs."),
        new("automatic", "Automatic", ReportFieldKind.Category, null, "'yes' for a bolus the algorithm gave."),
        new("detail", "Detail", ReportFieldKind.Category, null,
            "The bolus type, the temp basal's origin, the override's name, the insulin of an injection, or a note's text (cut to 120 characters)."),
    ];

    public ReportBindings? DefaultBindings(ReportPrimitive primitive) => primitive == ReportPrimitive.TimeSeries
        ? new ReportBindings { X = "time", Y = ["insulin"] }
        : null;

    public IReadOnlyList<ReportPrimitive> AllowedPrimitives { get; } = [ReportPrimitive.Table, ReportPrimitive.TimeSeries];

    public TimeSpan DefaultRange => TimeSpan.FromHours(24);

    public TimeSpan MaxRange(ReportPrimitive primitive) => TimeSpan.FromDays(14);

    public int PointBudget => Budget;

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> SampleRows { get; } =
    [
        new Dictionary<string, object?> { ["time"] = 1793260800000L, ["type"] = "carbs", ["carbs"] = 45.0, ["detail"] = null },
        new Dictionary<string, object?> { ["time"] = 1793260800000L, ["type"] = "bolus", ["insulin"] = 4.5, ["automatic"] = "no", ["detail"] = "MealBolus" },
        new Dictionary<string, object?> { ["time"] = 1793262600000L, ["type"] = "temp_basal", ["rate"] = 1.4, ["durationMinutes"] = 30.0, ["detail"] = "Algorithm" },
    ];

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters)
    {
        var p = (TreatmentsParameters)parameters;
        if (p.Types is { Count: 0 })
            yield return ("types", "Send at least one kind, or leave 'types' out for every kind.");
        if (p.MinCarbs is < 0 or > 1000)
            yield return ("minCarbs", "Send 0 to 1000 grams.");
        if (p.MinInsulin is < 0 or > 100)
            yield return ("minInsulin", "Send 0 to 100 units.");
    }

    /// <summary>One record as a row, before the fields are picked.</summary>
    internal sealed record Record(
        long Time,
        ReportTreatmentType Type,
        double? Insulin = null,
        double? Carbs = null,
        double? Rate = null,
        double? DurationMinutes = null,
        bool? Automatic = null,
        string? Detail = null);

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var parameters = (TreatmentsParameters)request.Parameters;
        var records = await ReadAsync(request.Services, Kinds(parameters), request.From.UtcDateTime, request.To.UtcDateTime, cancellationToken);
        var kept = Filter(records, parameters);
        if (records.Count > 0)
            request.ReadLog.Record("treatments", records.Count);

        var latest = kept.OrderByDescending(r => r.Time).Take(Budget).OrderBy(r => r.Time).ToList();
        var summary = Summarise(kept, request.From, request.To, Kinds(parameters),
            request.Services.GetRequiredService<IStatisticsService>(), latest.Count);

        return new ReportDatasetOutput
        {
            Rows = [.. latest.Select(Row)],
            Summary = summary,
            Resolution = new ReportResolution(0, 0, kept.Count, latest.Count, latest.Count < kept.Count),
        };
    }

    /// <summary>The kinds a request names, every kind when it names none.</summary>
    internal static IReadOnlySet<ReportTreatmentType> Kinds(TreatmentsParameters parameters) =>
        (parameters.Types is { Count: > 0 } types ? types : Enum.GetValues<ReportTreatmentType>()).ToHashSet();

    /// <summary>The records of <paramref name="kinds"/> from <paramref name="from"/> to <paramref name="to"/>, as rows before filtering.</summary>
    internal static async Task<List<Record>> ReadAsync(
        IServiceProvider services, IReadOnlySet<ReportTreatmentType> kinds, DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = new List<Record>();
        if (kinds.Contains(ReportTreatmentType.Bolus))
            rows.AddRange((await services.GetRequiredService<IBolusRepository>().GetAsync(from, to, null, null, RecordLimit, ct: ct))
                .Select(b => new Record(b.Mills, ReportTreatmentType.Bolus, Insulin: b.Insulin, Automatic: b.Automatic, Detail: b.BolusType?.ToString())));
        if (kinds.Contains(ReportTreatmentType.Carbs))
            rows.AddRange((await services.GetRequiredService<ICarbIntakeRepository>().GetAsync(from, to, null, null, RecordLimit, ct: ct))
                .Select(c => new Record(c.Mills, ReportTreatmentType.Carbs, Carbs: c.Carbs)));
        if (kinds.Contains(ReportTreatmentType.TempBasal))
            rows.AddRange((await services.GetRequiredService<ITempBasalRepository>().GetAsync(from, to, null, null, RecordLimit, ct: ct))
                .Select(t => new Record(t.StartMills, ReportTreatmentType.TempBasal, Rate: t.Rate, DurationMinutes: Minutes(t.StartTimestamp, t.EndTimestamp),
                    Detail: t.Origin.ToString())));
        if (kinds.Contains(ReportTreatmentType.Note))
            rows.AddRange((await services.GetRequiredService<INoteRepository>().GetAsync(from, to, null, null, RecordLimit, ct: ct))
                .Select(n => new Record(n.Mills, ReportTreatmentType.Note, Detail: Cut(n.Text))));
        if (kinds.Contains(ReportTreatmentType.Override))
            rows.AddRange((await services.GetRequiredService<IStateSpanService>().GetStateSpansInCategoriesAsync(
                    [StateSpanCategory.Override], null, from, to, null, null, RecordLimit, 0, true, ct))
                .Select(s => new Record(s.StartMills, ReportTreatmentType.Override, DurationMinutes: Minutes(s.StartTimestamp, s.EndTimestamp),
                    Detail: Cut(s.State))));
        if (kinds.Contains(ReportTreatmentType.BasalInjection))
            rows.AddRange((await services.GetRequiredService<IBasalInjectionRepository>().GetAsync(from, to, null, null, RecordLimit, 0, true, ct))
                .Select(i => new Record(i.Mills, ReportTreatmentType.BasalInjection, Insulin: i.Units, Detail: Cut(i.InsulinContext?.InsulinName))));
        return rows;
    }

    /// <summary>The records the amount filters keep: each applies to its own kinds only.</summary>
    internal static List<Record> Filter(IEnumerable<Record> records, TreatmentsParameters parameters) =>
        [.. records.Where(r => r.Type switch
        {
            ReportTreatmentType.Carbs => parameters.MinCarbs is not { } min || r.Carbs >= min,
            ReportTreatmentType.Bolus or ReportTreatmentType.BasalInjection => parameters.MinInsulin is not { } min || r.Insulin >= min,
            _ => true,
        }).OrderBy(r => r.Time)];

    internal static IReadOnlyDictionary<string, object?> Row(Record r) => new Dictionary<string, object?>
    {
        ["time"] = r.Time,
        ["type"] = Wire(r.Type),
        ["insulin"] = r.Insulin is { } u ? Math.Round(u, 2) : null,
        ["carbs"] = r.Carbs is { } g ? Math.Round(g, 1) : null,
        ["rate"] = r.Rate is { } rate ? Math.Round(rate, 3) : null,
        ["durationMinutes"] = r.DurationMinutes is { } m ? Math.Round(m) : null,
        ["automatic"] = r.Automatic switch { true => "yes", false => "no", null => null },
        ["detail"] = r.Detail,
    };

    internal static string Wire(ReportTreatmentType type) => type switch
    {
        ReportTreatmentType.Bolus => "bolus",
        ReportTreatmentType.Carbs => "carbs",
        ReportTreatmentType.TempBasal => "temp_basal",
        ReportTreatmentType.Note => "note",
        ReportTreatmentType.Override => "override",
        ReportTreatmentType.BasalInjection => "basal_injection",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    private static double? Minutes(DateTime start, DateTime? end) => end is { } e ? (e - start).TotalMinutes : null;

    private static string? Cut(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null
        : text.Trim() is var t && t.Length > MaxDetailLength ? t[..MaxDetailLength] + "..." : text.Trim();

    /// <summary>
    /// Counts and totals per kind, the largest bolus and carb entry, the latest of each: never the rows
    /// themselves, so the text stays short however many records the range holds.
    /// </summary>
    internal static string Summarise(
        IReadOnlyList<Record> records,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlySet<ReportTreatmentType> kinds,
        IStatisticsService statistics,
        int rowsServed)
    {
        static string At(long mills) => DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        static string N(double value, string format = "0.##") => value.ToString(format, CultureInfo.InvariantCulture);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Treatments from {from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm} UTC.").Append('\n');
        if (records.Count == 0)
            return text.Append("No treatment records of these kinds in this period.").ToString();

        var boluses = records.Where(r => r.Type == ReportTreatmentType.Bolus).ToList();
        var carbs = records.Where(r => r.Type == ReportTreatmentType.Carbs).ToList();
        var summary = statistics.CalculateTreatmentSummary(
            boluses.Select(b => new Core.Models.V4.Bolus { Insulin = b.Insulin ?? 0, Automatic = b.Automatic ?? false }),
            carbs.Select(c => new Core.Models.V4.CarbIntake { Carbs = c.Carbs ?? 0 }));

        if (kinds.Contains(ReportTreatmentType.Bolus))
        {
            text.Append(CultureInfo.InvariantCulture, $"Boluses: {boluses.Count}");
            if (boluses.Count > 0)
            {
                var automatic = boluses.Where(b => b.Automatic == true).ToList();
                var largest = boluses.MaxBy(b => b.Insulin)!;
                text.Append(CultureInfo.InvariantCulture, $", {N(summary.Totals.Insulin.Bolus)} U in all");
                if (automatic.Count > 0)
                    text.Append(CultureInfo.InvariantCulture, $" ({automatic.Count} given by the algorithm)");
                text.Append(CultureInfo.InvariantCulture, $". Largest {N(largest.Insulin!.Value)} U at {At(largest.Time)}. Latest {N(boluses[^1].Insulin!.Value)} U at {At(boluses[^1].Time)}");
            }
            text.Append(".\n");
        }

        if (kinds.Contains(ReportTreatmentType.Carbs))
        {
            text.Append(CultureInfo.InvariantCulture, $"Carb entries: {carbs.Count}");
            if (carbs.Count > 0)
            {
                var largest = carbs.MaxBy(c => c.Carbs)!;
                text.Append(CultureInfo.InvariantCulture, $", {N(summary.Totals.Food.Carbs, "0")} g in all. Largest {N(largest.Carbs!.Value, "0")} g at {At(largest.Time)}. "
                    + $"Latest {N(carbs[^1].Carbs!.Value, "0")} g at {At(carbs[^1].Time)}");
            }
            text.Append(".\n");
        }

        void CountOf(ReportTreatmentType type, string words)
        {
            if (!kinds.Contains(type))
                return;
            var of = records.Where(r => r.Type == type).ToList();
            text.Append(CultureInfo.InvariantCulture, $"{words}: {of.Count}");
            if (of.Count > 0)
                text.Append(CultureInfo.InvariantCulture, $". Latest at {At(of[^1].Time)}");
            text.Append(".\n");
        }

        CountOf(ReportTreatmentType.TempBasal, "Temp basals");
        CountOf(ReportTreatmentType.Override, "Overrides");
        CountOf(ReportTreatmentType.Note, "Notes");
        if (kinds.Contains(ReportTreatmentType.BasalInjection))
        {
            var injections = records.Where(r => r.Type == ReportTreatmentType.BasalInjection).ToList();
            text.Append(CultureInfo.InvariantCulture, $"Long-acting injections: {injections.Count}");
            if (injections.Count > 0)
                text.Append(CultureInfo.InvariantCulture, $". Latest {N(injections[^1].Insulin ?? 0)} U at {At(injections[^1].Time)}");
            text.Append(".\n");
        }

        if (rowsServed < records.Count)
            text.Append(CultureInfo.InvariantCulture, $"The rows hold the latest {rowsServed} of {records.Count} records. The counts above cover all of them.\n");
        return text.ToString().TrimEnd();
    }
}
