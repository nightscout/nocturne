using System.Globalization;
using Nocturne.Core.Models;

namespace Nocturne.API.Reports.Datasets;

/// <summary>
/// A dataset whose rows are drawn in lanes as spans, each from its start to its end: the shape every lane dataset
/// shares, so its rows, sentences and cut are written once. A record of one instant (a site change, an alarm) is a
/// span whose end is its start; the chart draws it as a tick.
/// </summary>
internal abstract class LaneDataset : IReportDataset
{
    /// <summary>One span before it is a row: <paramref name="Lane"/> is the lane it is drawn in.</summary>
    internal sealed record Span(string Lane, string Label, long Start, long End, ChartColor Color);

    /// <summary>
    /// What a read found: its spans in start order, and whether the read stopped at a limit of its own before the end
    /// of the range (a number of sessions, say), so the spans hold only the first part of it.
    /// </summary>
    internal sealed record LaneRead(IReadOnlyList<Span> Spans, bool Cut = false);

    public abstract string Name { get; }

    public abstract string Label { get; }

    public abstract string Description { get; }

    public abstract Type ParametersType { get; }

    public abstract string RequiredScope { get; }

    /// <summary>What a lane is: the words of the <c>kind</c> field's description.</summary>
    protected abstract string LaneWords { get; }

    /// <summary>What a span's name is: the words of the <c>label</c> field's description.</summary>
    protected abstract string LabelWords { get; }

    /// <summary>What one row is, singular: "event", "alarm", "stage".</summary>
    protected abstract string Noun { get; }

    /// <summary>The source the read log names; null for spans worked out of a read the run's cache records.</summary>
    protected abstract string? ReadName { get; }

    /// <summary>
    /// Where a lane sits among the others, lowest first. The chart lays lanes out in the order their first rows come,
    /// so the rows are sent lane by lane in this order; by default, in the order the lanes first start.
    /// </summary>
    protected virtual int LaneRank(string lane) => 0;

    /// <summary>Each row is one instant: its end is its start.</summary>
    protected virtual bool Instants => false;

    public virtual TimeSpan DefaultRange => TimeSpan.FromDays(7);

    public abstract TimeSpan MaxRange { get; }

    public virtual int PointBudget => 2000;

    public IReadOnlyList<ReportField> Fields => _fields ??=
    [
        new("kind", "Kind", ReportFieldKind.Category, null, LaneWords),
        new("label", "Name", ReportFieldKind.Category, null, LabelWords, ColorField: "color"),
        new("start", Instants ? "Time" : "Start", ReportFieldKind.Time, null,
            Instants ? "When it happened, Unix milliseconds." : "When it starts, Unix milliseconds."),
        new("end", "End", ReportFieldKind.Time, null,
            Instants ? "The same as the start: it is one instant." : "When it ends, Unix milliseconds; the end of the range for one still running."),
        new("color", "Color", ReportFieldKind.Category, null, "The theme color it is drawn in."),
    ];

    private IReadOnlyList<ReportField>? _fields;

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Spans];

    public ReportLayerBindings? DefaultBindings(ReportMark mark) => mark == ReportMark.Spans
        ? new ReportLayerBindings { Start = "start", End = "end", Category = "kind" }
        : null;

    public virtual IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    /// <summary>The first spans in [<paramref name="from"/>, <paramref name="to"/>), at most <paramref name="limit"/> of them.</summary>
    protected abstract Task<LaneRead> ReadAsync(ReportDatasetRequest request, long from, long to, int limit, CancellationToken cancellationToken);

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var from = request.From.ToUnixTimeMilliseconds();
        var to = request.To.ToUnixTimeMilliseconds();
        var read = await ReadAsync(request, from, to, PointBudget + 1, cancellationToken);
        if (read.Spans.Count > 0 && ReadName is { } source)
            request.ReadLog.Record(source, read.Spans.Count);
        return Output(Clip(read.Spans, from, to, Instants), request, PointBudget, read.Cut);
    }

    /// <summary>Each span cut to the range, in start order; an instant is kept where it falls in it.</summary>
    internal static List<Span> Clip(IEnumerable<Span> spans, long from, long to, bool instants) =>
    [
        .. spans
            .Where(s => instants ? s.Start >= from && s.Start < to : s.Start < to && s.End > from)
            .Select(s => instants ? s with { End = s.Start } : s with { Start = Math.Max(s.Start, from), End = Math.Min(s.End, to) })
            .OrderBy(s => s.Start),
    ];

    /// <summary>
    /// The first <paramref name="budget"/> spans as rows, cut where there are more, or where the read stopped at a limit
    /// of its own (<see cref="LaneRead.Cut"/>, <paramref name="readCut"/>).
    /// </summary>
    internal ReportDatasetOutput Output(IReadOnlyList<Span> spans, ReportDatasetRequest request, int budget, bool readCut = false)
    {
        var kept = spans.Take(budget).ToList();
        var cut = kept.Count < spans.Count || (readCut && kept.Count > 0);
        var window = ReportFacts.Window(request.From, request.To, request.Clock);
        var lanes = kept.GroupBy(s => s.Lane).Select(g => (Lane: g.Key, Count: g.Count())).ToList();
        var lines = new List<string> { string.Create(CultureInfo.InvariantCulture, $"{Label} from {window}.") };
        lines.AddRange(lanes.Select(l => $"{l.Lane}: {ReportFacts.Plural(l.Count, Noun)}."));
        if (kept.Count > 0)
        {
            var latest = kept[^1];
            lines.Add($"Latest: {latest.Label} at {ReportFacts.Instant(DateTimeOffset.FromUnixTimeMilliseconds(latest.Start), request.Clock)}.");
        }
        string? cutNote = null;
        if (cut)
        {
            cutNote = $"Only the first {kept.Count} {Noun}s are shown, to {ReportFacts.Instant(DateTimeOffset.FromUnixTimeMilliseconds(kept[^1].Start), request.Clock)}.";
            lines.Add(cutNote);
        }

        var sentence = kept.Count == 0 ? $"No {Noun}s in this period." : $"{ReportFacts.Plural(kept.Count, Noun)} in this period.";
        return new ReportDatasetOutput
        {
            Rows = [.. kept.OrderBy(s => LaneRank(s.Lane)).Select(s => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["kind"] = s.Lane,
                ["label"] = s.Label,
                ["start"] = s.Start,
                ["end"] = s.End,
                ["color"] = ReportWire.Name(s.Color),
            })],
            Summary = string.Join('\n', lines),
            Facts = [.. lanes.Take(ReportFacts.Max).Select(l => ReportFacts.Count(l.Lane, l.Count).Of("start", "end"))],
            Sentence = sentence,
            CutNote = cutNote,
            Context = $"{window}. {ReportFacts.Plural(kept.Count, Noun)}.",
            Resolution = new ReportResolution(0, 0, spans.Count, kept.Count, cut),
        };
    }

    protected static long Mills(DateTime at) => new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    protected static DateTime Utc(long mills) => DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;
}
