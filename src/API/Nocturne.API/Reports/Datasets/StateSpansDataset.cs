using System.Globalization;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Reports.Datasets;

/// <summary>
/// The state spans the dashboard chart draws in its lanes (pump modes, profiles, overrides,
/// activities and temp basals), read through the chart's own stages (<see cref="ReportRunCache.ChartAsync"/>)
/// and redacted as its endpoint redacts: a kind whose scope the token lacks is left out and named. The kinds the
/// chart does not draw (temporary targets, pump connection, data left out, motion) are read from the state spans
/// themselves, each under its category's read scope (<see cref="StateSpanReadScopeGuard"/>).
/// </summary>
internal sealed class StateSpansDataset : IReportDataset
{
    public const int Budget = 2000;

    public string Name => StateSpansParameters.Name;

    public string Label => "State spans";

    public string Description => "Pump modes, profiles, overrides, activities, temp basals, temporary targets, pump connection, data left out and motion, each from its start to its end.";

    public Type ParametersType => typeof(StateSpansParameters);

    public string RequiredScope => Scope.TreatmentsRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("kind", "Kind", ReportFieldKind.Category, null, "Pump mode, Profile, Override, Activity, Temp basal, Temporary target, Pump connection, Data left out or Motion: the lane the span is drawn in."),
        new("label", "Name", ReportFieldKind.Category, null, "The mode, profile, override, activity, target, connection state or motion, or a temp basal's rate.", ColorField: "color"),
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

    /// <summary>The kinds read when none are named: the ones the dashboard chart draws, as before the stored kinds were added.</summary>
    internal static readonly IReadOnlyList<ReportSpanKind> ChartKinds = [.. Enum.GetValues<ReportSpanKind>().Where(k => CategoryOf(k) is null)];

    /// <summary>The scope each kind needs, as the chart's endpoint redacts it; an activity of sleep needs sleep.read in place of treatments.read.</summary>
    internal static string ScopeOf(ReportSpanKind kind) => kind switch
    {
        ReportSpanKind.PumpMode => Scope.DevicesRead,
        ReportSpanKind.Profile => Scope.TherapyRead,
        _ when CategoryOf(kind) is { } category => StateSpanReadScopeGuard.RequiredReadScope(category),
        _ => Scope.TreatmentsRead,
    };

    /// <summary>The state span category a kind the chart does not draw is read from; null for a kind the chart draws.</summary>
    internal static StateSpanCategory? CategoryOf(ReportSpanKind kind) => kind switch
    {
        ReportSpanKind.TemporaryTarget => StateSpanCategory.TemporaryTarget,
        ReportSpanKind.PumpConnectivity => StateSpanCategory.PumpConnectivity,
        ReportSpanKind.DataExclusion => StateSpanCategory.DataExclusion,
        ReportSpanKind.Motion => StateSpanCategory.Motion,
        _ => null,
    };

    /// <summary>The theme color of a span of a kind the chart does not draw, by its state.</summary>
    internal static ChartColor ColorOf(ReportSpanKind kind, string? state) => kind switch
    {
        ReportSpanKind.TemporaryTarget => ChartColor.Chart3,
        ReportSpanKind.PumpConnectivity => state switch
        {
            nameof(PumpConnectivityState.Connected) => ChartColor.PumpModeAutomatic,
            nameof(PumpConnectivityState.Removed) => ChartColor.PumpModeOff,
            nameof(PumpConnectivityState.BluetoothOff) => ChartColor.SystemEventWarning,
            _ => ChartColor.PumpModeSuspended,
        },
        ReportSpanKind.Motion => ChartColor.Steps,
        _ => ChartColor.MutedForeground,
    };

    internal static string Words(ReportSpanKind kind) => kind switch
    {
        ReportSpanKind.PumpMode => "Pump mode",
        ReportSpanKind.Profile => "Profile",
        ReportSpanKind.Override => "Override",
        ReportSpanKind.Activity => "Activity",
        ReportSpanKind.TempBasal => "Temp basal",
        ReportSpanKind.TemporaryTarget => "Temporary target",
        ReportSpanKind.PumpConnectivity => "Pump connection",
        ReportSpanKind.DataExclusion => "Data left out",
        ReportSpanKind.Motion => "Motion",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The dataset a span is read from, as the read log names it.</summary>
    private static string ReadOf(Span span) => span.Kind switch
    {
        ReportSpanKind.PumpMode => "pump_modes",
        ReportSpanKind.Profile => "profiles",
        ReportSpanKind.Activity when span.Sleep => "sleep",
        ReportSpanKind.PumpConnectivity => "pump_connectivity",
        ReportSpanKind.DataExclusion => "data_exclusions",
        _ => "treatments",
    };

    /// <summary>One span before it is a row.</summary>
    internal sealed record Span(ReportSpanKind Kind, string Label, long Start, long End, ChartColor Color, bool Sleep = false);

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var parameters = (StateSpansParameters)request.Parameters;
        var kinds = parameters.Kinds is { Count: > 0 } asked ? asked : ChartKinds;
        var from = request.From.ToUnixTimeMilliseconds();
        var to = request.To.ToUnixTimeMilliseconds();
        var withheld = kinds.Where(k => !request.HasScope(ScopeOf(k))).ToList();

        var spans = kinds.Any(k => CategoryOf(k) is null)
            ? Spans(await request.Cache.ChartAsync(request, cancellationToken), kinds, from, to)
            : [];
        var stored = kinds.Where(k => CategoryOf(k) is not null && !withheld.Contains(k)).ToList();
        if (stored.Count > 0)
        {
            var read = await request.Services.GetRequiredService<IStateSpanService>().GetStateSpansInCategoriesAsync(
                [.. stored.Select(k => CategoryOf(k)!.Value)], null, request.From.UtcDateTime, request.To.UtcDateTime, null, null,
                TreatmentsDataset.RecordLimit, 0, false, cancellationToken);
            spans.AddRange(Stored(read, stored, from, to));
            spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        }
        var kept = spans.OrderBy(s => s.Start).Take(Budget).ToList();
        foreach (var (dataset, count) in kept.GroupBy(ReadOf).Select(g => (g.Key, g.Count())))
            request.ReadLog.Record(dataset, count);
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
            string.Create(CultureInfo.InvariantCulture, $"State spans from {ReportFacts.Window(request.From, request.To, request.Zone)}."),
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
            Facts = [.. counts.Select(c => ReportFacts.Count(Words(c.Kind), c.Count, "spans").Of("start", "end"))],
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
                    spans.Add(new Span(kind, string.IsNullOrWhiteSpace(s.State) ? Words(kind) : s.State, start, end, s.Color, s.Kind == ChartSpanKind.Sleep));
            }
        }
        Add(ReportSpanKind.PumpMode, data.PumpModeSpans);
        Add(ReportSpanKind.Profile, data.ProfileSpans);
        Add(ReportSpanKind.Override, data.OverrideSpans);
        Add(ReportSpanKind.Activity, data.ActivitySpans);
        Add(ReportSpanKind.TempBasal, data.TempBasalSpans);
        return spans;
    }

    /// <summary>The stored spans of <paramref name="kinds"/> (kinds the chart does not draw), each cut to the range as <see cref="Spans"/> cuts them.</summary>
    internal static IEnumerable<Span> Stored(IEnumerable<StateSpan> read, IReadOnlyCollection<ReportSpanKind> kinds, long from, long to)
    {
        foreach (var s in read)
        {
            var matched = kinds.Where(k => CategoryOf(k) == s.Category).ToList();
            if (matched.Count == 0)
                continue;
            var kind = matched[0];
            var start = Math.Max(s.StartMills, from);
            var end = Math.Min(s.EndMills ?? to, to);
            if (end > start)
                yield return new Span(kind, StoredLabel(kind, s.State), start, end, ColorOf(kind, s.State));
        }
    }

    /// <summary>A stored span's name: its state in words, or the kind where it has none.</summary>
    private static string StoredLabel(ReportSpanKind kind, string? state) => (kind, state) switch
    {
        (_, null or "") => Words(kind),
        (ReportSpanKind.PumpConnectivity, nameof(PumpConnectivityState.BluetoothOff)) => "Bluetooth off",
        (ReportSpanKind.TemporaryTarget, nameof(TemporaryTargetState.Active)) => "Temporary target",
        _ => state,
    };
}
