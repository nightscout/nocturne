using System.Globalization;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Reports.Datasets;

/// <summary>
/// The state spans the dashboard chart draws in its lanes (pump modes, profiles, overrides,
/// activities and temp basals), read through the chart's own stages (<see cref="ReportRunCache.ChartAsync"/>)
/// and redacted as its endpoint redacts: a kind whose scope the token lacks is left out and named.
/// </summary>
internal sealed class StateSpansDataset : IReportDataset
{
    public const int Budget = 2000;

    public string Name => StateSpansParameters.Name;

    public string Label => "State spans";

    public string Description => "Pump modes, profiles, overrides, activities and temp basals, each from its start to its end.";

    public Type ParametersType => typeof(StateSpansParameters);

    public string RequiredScope => Scope.TreatmentsRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("kind", "Kind", ReportFieldKind.Category, null, "Pump mode, Profile, Override, Activity or Temp basal: the lane the span is drawn in."),
        new("label", "Name", ReportFieldKind.Category, null, "The mode, profile, override or activity, or a temp basal's rate.", ColorField: "color"),
        new("start", "Start", ReportFieldKind.Time, null, "When the span starts, Unix milliseconds."),
        new("end", "End", ReportFieldKind.Time, null, "When the span ends, Unix milliseconds; the end of the range for a span still running."),
        new("color", "Color", ReportFieldKind.Category, null, "The span's theme color. It is drawn in this color."),
    ];

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Spans];

    public ReportLayerBindings? DefaultBindings(ReportMark mark) => mark == ReportMark.Spans
        ? new ReportLayerBindings { Start = "start", End = "end", Category = "kind" }
        : null;

    public TimeSpan DefaultRange => TimeSpan.FromHours(24);

    public TimeSpan MaxRange => TimeSpan.FromDays(14);

    public int PointBudget => Budget;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters)
    {
        if (((StateSpansParameters)parameters).Kinds is { } kinds && kinds.Distinct().Count() != kinds.Count)
            yield return ("kinds", "A kind is named twice.");
    }

    /// <summary>The scope each kind needs, as the chart's endpoint redacts it; an activity of sleep needs sleep.read in place of treatments.read.</summary>
    internal static string ScopeOf(ReportSpanKind kind) => kind switch
    {
        ReportSpanKind.PumpMode => Scope.DevicesRead,
        ReportSpanKind.Profile => Scope.TherapyRead,
        _ => Scope.TreatmentsRead,
    };

    internal static string Words(ReportSpanKind kind) => kind switch
    {
        ReportSpanKind.PumpMode => "Pump mode",
        ReportSpanKind.Profile => "Profile",
        ReportSpanKind.Override => "Override",
        ReportSpanKind.Activity => "Activity",
        ReportSpanKind.TempBasal => "Temp basal",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>One span before it is a row.</summary>
    internal sealed record Span(ReportSpanKind Kind, string Label, long Start, long End, ChartColor Color);

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var parameters = (StateSpansParameters)request.Parameters;
        var kinds = parameters.Kinds is { Count: > 0 } asked ? asked : Enum.GetValues<ReportSpanKind>();
        var data = await request.Cache.ChartAsync(request, cancellationToken);
        var from = request.From.ToUnixTimeMilliseconds();
        var to = request.To.ToUnixTimeMilliseconds();

        var spans = Spans(data, kinds, from, to);
        var withheld = kinds.Where(k => !request.HasScope(ScopeOf(k))).ToList();
        var kept = spans.OrderBy(s => s.Start).Take(Budget).ToList();
        var rows = kept.Select(s => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["kind"] = Words(s.Kind),
            ["label"] = s.Label,
            ["start"] = s.Start,
            ["end"] = s.End,
            ["color"] = ReportWire.Name(s.Color),
        }).ToList();

        var counts = kinds.Where(k => !withheld.Contains(k))
            .Select(k => (Kind: k, Count: spans.Count(s => s.Kind == k)))
            .ToList();
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"State spans from {request.From:yyyy-MM-dd HH:mm} to {request.To:yyyy-MM-dd HH:mm} UTC."),
        };
        lines.AddRange(counts.Select(c => $"{Words(c.Kind)}: {ReportFacts.Plural(c.Count, "span")}."));
        if (withheld.Count > 0)
            lines.Add($"Not shown, because the token does not hold the scope they need: {string.Join(", ", withheld.Select(Words))}.");
        if (kept.Count < spans.Count)
            lines.Add($"The rows hold the first {kept.Count} of {spans.Count} spans.");

        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = string.Join('\n', lines),
            Facts = [.. counts.Select(c => ReportFacts.Count(Words(c.Kind), c.Count, "spans"))],
            Sentence = spans.Count == 0 ? "No state spans in this period." : $"{ReportFacts.Plural(spans.Count, "span")} in this period.",
            Context = $"{ReportFacts.Window(request.From, request.To, request.Zone)}. {ReportFacts.Plural(spans.Count, "span")}.",
            Resolution = new ReportResolution(0, 0, spans.Count, kept.Count, kept.Count < spans.Count),
        };
    }

    /// <summary>The redacted payload's spans of <paramref name="kinds"/>, each cut to the range; one still running ends at the range's end.</summary>
    internal static List<Span> Spans(DashboardChartData data, IReadOnlyCollection<ReportSpanKind> kinds, long from, long to)
    {
        var spans = new List<Span>();
        void Add(ReportSpanKind kind, IEnumerable<ChartStateSpanDto> source)
        {
            if (!kinds.Contains(kind))
                return;
            foreach (var s in source)
            {
                var start = Math.Max(s.StartMills, from);
                var end = Math.Min(s.EndMills ?? to, to);
                if (end > start)
                    spans.Add(new Span(kind, string.IsNullOrWhiteSpace(s.State) ? Words(kind) : s.State, start, end, s.Color));
            }
        }
        Add(ReportSpanKind.PumpMode, data.PumpModeSpans);
        Add(ReportSpanKind.Profile, data.ProfileSpans);
        Add(ReportSpanKind.Override, data.OverrideSpans);
        Add(ReportSpanKind.Activity, data.ActivitySpans);
        Add(ReportSpanKind.TempBasal, data.TempBasalSpans);
        return spans;
    }
}
