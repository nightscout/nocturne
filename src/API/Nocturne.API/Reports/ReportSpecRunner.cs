using Nocturne.Core.Models.Authorization;
using System.Globalization;
using System.Text;

namespace Nocturne.API.Reports;

/// <summary>The caller a report runs for: its scopes, its services and its read-audit log.</summary>
internal sealed record ReportRunContext(
    bool UseMmol,
    IReadOnlySet<string> GrantedScopes,
    IServiceProvider Services,
    ReportReadLog ReadLog,
    bool HistoryClamped = false,
    TimeZoneInfo? Zone = null)
{
    public bool HasScope(string scope) => Scope.Satisfies(GrantedScopes, scope);

    /// <summary>What the run's panels have read, shared between panels over the same range.</summary>
    public Datasets.ReportRunCache Cache { get; } = new();
}

/// <summary>
/// Runs a normalised <see cref="ReportSpec"/>, one panel after another: resolves the range,
/// checks the dataset's scope, runs the dataset on the request's services (so under the request's
/// reader), keeps the bound fields the token may read, and joins the panel summaries.
/// </summary>
/// <remarks>
/// Panels run in turn, not together: the datasets share the request's scoped services, which are
/// not made for concurrent use. The whole report has <see cref="Timeout"/>; panels not finished by
/// then come back <see cref="ReportPanelStatus.TimedOut"/> and named in a warning, beside the
/// finished ones. A dataset that throws fails only its own panel (<see cref="ReportPanelStatus.Failed"/>),
/// in the same way.
/// </remarks>
internal sealed class ReportSpecRunner(IReportDatasetRegistry registry, TimeProvider time, ILogger<ReportSpecRunner>? logger = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>How far back a history-clamped caller reads; the database clamps reads to the same window.</summary>
    public static readonly TimeSpan ClampedHistory = TimeSpan.FromHours(24);

    internal const string FailedNote = "Not shown: an error occurred when this panel was read. The other panels are not affected.";

    internal const string LimitedNote = "Limited to the last 24 hours: this token does not read older data.";

    public async Task<ReportResult> RunAsync(ReportSpec spec, ReportRunContext context, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var now = time.GetUtcNow();

        var panels = new List<ReportPanelResult>(spec.Panels.Count);
        foreach (var panel in spec.Panels)
        {
            var dataset = registry.Find(panel.Dataset.DatasetName)
                ?? throw new InvalidOperationException($"The validated spec names an unregistered dataset '{panel.Dataset.DatasetName}'.");
            var (from, to) = Resolve(panel.Range!, now);
            var limited = false;
            if (context.HistoryClamped && from < now - ClampedHistory)
            {
                limited = true;
                from = now - ClampedHistory;
                if (to <= from)
                {
                    panels.Add(Empty(panel, dataset, from, from, ReportPanelStatus.NotPermitted,
                        "Not shown: the range ends more than 24 hours ago, and this token reads only the last 24 hours."));
                    continue;
                }
            }

            if (deadline.IsCancellationRequested)
            {
                panels.Add(TimedOut(panel, dataset, from, to));
                continue;
            }

            if (!context.HasScope(dataset.RequiredScope))
            {
                panels.Add(Empty(panel, dataset, from, to, ReportPanelStatus.NotPermitted,
                    $"Not shown: the token does not hold the '{dataset.RequiredScope}' scope that {dataset.Name} needs."));
                continue;
            }

            ReportDatasetOutput output;
            try
            {
                output = await dataset.RunAsync(
                    new ReportDatasetRequest(panel.Dataset, panel.Primitive, from, to, context.UseMmol,
                        context.GrantedScopes, context.Services, context.ReadLog, context.HistoryClamped, context.Cache, now, panel.Bindings, context.Zone),
                    linked.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                panels.Add(TimedOut(panel, dataset, from, to));
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogError(ex, "Report dataset {Dataset} failed for panel {Panel}", dataset.Name, panel.Id);
                panels.Add(Empty(panel, dataset, from, to, ReportPanelStatus.Failed, FailedNote));
                continue;
            }

            var resolved = Resolve(panel, dataset, from, to, output, context.UseMmol);
            panels.Add(limited ? resolved with { Summary = resolved.Summary + '\n' + LimitedNote, Limited = true } : resolved);
        }

        panels = [.. panels.Select((result, i) => AtLimit(result, spec.Panels[i], now))];

        var warnings = new List<string>();
        var timedOut = panels.Where(p => p.Status == ReportPanelStatus.TimedOut).Select(p => p.Id).ToList();
        if (timedOut.Count > 0)
            warnings.Add(
                $"{(timedOut.Count == 1 ? "Panel" : "Panels")} {string.Join(", ", timedOut)} did not finish in "
                + $"{Timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds and {(timedOut.Count == 1 ? "has" : "have")} no data. "
                + "Ask for a shorter range or fewer panels.");
        var failed = panels.Where(p => p.Status == ReportPanelStatus.Failed).Select(p => p.Id).ToList();
        if (failed.Count > 0)
            warnings.Add($"{(failed.Count == 1 ? "Panel" : "Panels")} {string.Join(", ", failed)}: {FailedNote}");
        var limitedPanels = panels.Where(p => p.Limited).Select(p => p.Id).ToList();
        if (limitedPanels.Count > 0)
            warnings.Add($"{(limitedPanels.Count == 1 ? "Panel" : "Panels")} {string.Join(", ", limitedPanels)}: {LimitedNote}");

        return new ReportResult
        {
            Spec = spec,
            Units = context.UseMmol ? "mmol" : "mg/dl",
            GlucoseColorMode = ReportResult.DefaultGlucoseColorMode,
            Panels = panels,
            Summary = JoinSummaries(spec, panels),
            Warnings = warnings,
        };
    }

    /// <summary>
    /// The panel marked at its dataset's longest range, with the sentence that says how long that is, so a
    /// page offers no longer range than the dataset draws.
    /// </summary>
    private ReportPanelResult AtLimit(ReportPanelResult result, ReportPanelSpec panel, DateTimeOffset now)
    {
        var dataset = registry.Find(panel.Dataset.DatasetName)!;
        var max = dataset.MaxRange(panel.Primitive);
        var (from, to) = Resolve(panel.Range!, now);
        if (to - from < max - TimeSpan.FromMinutes(1))
            return result;
        return result with { AtRangeLimit = true, RangeLimit = $"This panel covers at most {ReportSpecValidator.Describe(max)}." };
    }

    internal static (DateTimeOffset From, DateTimeOffset To) Resolve(ReportRange range, DateTimeOffset now) =>
        range.Hours is { } hours ? (now.AddHours(-hours), now) : (range.From!.Value, range.To!.Value);

    /// <summary>The field names a panel's bindings draw, in drawing order.</summary>
    internal static IReadOnlyList<string> BoundFields(ReportBindings bindings) =>
        [.. new[] { bindings.X }.OfType<string>()
            .Concat(bindings.Y ?? [])
            .Concat(bindings.Band ?? [])
            .Concat(bindings.Columns ?? [])
            .Concat(new[] { bindings.Series }.OfType<string>())
            .Distinct(StringComparer.Ordinal)];

    private static ReportPanelResult Resolve(
        ReportPanelSpec panel,
        IReportDataset dataset,
        DateTimeOffset from,
        DateTimeOffset to,
        ReportDatasetOutput output,
        bool useMmol)
    {
        var omitted = output.Omitted.Select(o => o.Field).ToHashSet(StringComparer.Ordinal);
        var catalogue = dataset.FieldsFor(panel.Primitive);
        var bound = BoundFields(panel.Bindings!);
        // A bar category's colour travels with it, so the view colours the bar from the row.
        var colours = bound
            .Select(name => catalogue.Single(f => f.Name == name).ColorField)
            .OfType<string>()
            .Where(name => catalogue.Any(f => f.Name == name));
        var companions = catalogue.Where(f => f.Companion).Select(f => f.Name);
        var fields = bound.Concat(colours).Concat(companions).Distinct(StringComparer.Ordinal)
            .Where(name => !omitted.Contains(name))
            .Select(name => catalogue.Single(f => f.Name == name).ResolveUnit(useMmol))
            .Select(f => output.FieldLabels?.GetValueOrDefault(f.Name) is { } label ? f with { Label = label } : f)
            .ToList();
        var names = fields.Select(f => f.Name).ToList();

        return new ReportPanelResult
        {
            Id = panel.Id!,
            Title = panel.Title,
            Dataset = dataset.Name,
            Primitive = panel.Primitive,
            Bindings = panel.Bindings!,
            Status = ReportPanelStatus.Ok,
            Window = output.Window ?? new ReportWindow(from.ToUnixTimeMilliseconds(), to.ToUnixTimeMilliseconds()),
            Fields = fields,
            Rows = [.. output.Rows.Select(row => (IReadOnlyDictionary<string, object?>)names.ToDictionary(n => n, n => row.GetValueOrDefault(n)))],
            Thresholds = output.Thresholds ?? (fields.Any(f => f.GlucoseScale) ? ReportThresholds.Of(null, useMmol) : null),
            Chart = panel.Primitive == ReportPrimitive.GlucoseChart ? output.Chart : null,
            OmittedScopes = output.OmittedScopes,
            Summary = output.Summary,
            Omitted = output.Omitted,
            Resolution = output.Resolution,
            Markers = panel.Primitive == ReportPrimitive.TimeSeries ? MarkersFor(fields, output.Markers) : [],
            Cohorts = output.Cohorts,
            Spans = panel.Primitive is ReportPrimitive.RangeBands or ReportPrimitive.GlucoseChart
                ? ReportBandSpan.Of([.. (panel.Bindings!.Band ?? []).Select(name => catalogue.Single(f => f.Name == name))])
                : [],
            Facts = output.Facts.Count > ReportFacts.Max ? [.. output.Facts.Take(ReportFacts.Max)] : output.Facts,
            Sentence = output.Sentence,
            Context = output.Context,
            Traces = panel.Primitive == ReportPrimitive.GlucoseChart ? output.Traces : [],
            LinkQuery = output.LinkQuery,
        };
    }

    /// <summary>
    /// The markers a time series draws: every one over a line; where the panel binds marker fields
    /// (a treatments panel's 'insulin' or 'carbs'), only the markers of those fields' kinds.
    /// </summary>
    internal static IReadOnlyList<ReportMarker> MarkersFor(IReadOnlyList<ReportField> fields, IReadOnlyList<ReportMarker> markers)
    {
        var kinds = fields.Where(f => f.Marker is not null).Select(f => f.Marker!.Value).ToHashSet();
        return kinds.Count == 0 ? markers : [.. markers.Where(m => kinds.Contains(m.Kind))];
    }

    private static ReportPanelResult TimedOut(ReportPanelSpec panel, IReportDataset dataset, DateTimeOffset from, DateTimeOffset to) =>
        Empty(panel, dataset, from, to, ReportPanelStatus.TimedOut, "Not shown: the report ran out of time before this panel.");

    private static ReportPanelResult Empty(
        ReportPanelSpec panel,
        IReportDataset dataset,
        DateTimeOffset from,
        DateTimeOffset to,
        ReportPanelStatus status,
        string summary) =>
        new()
        {
            Id = panel.Id!,
            Title = panel.Title,
            Dataset = dataset.Name,
            Primitive = panel.Primitive,
            Bindings = panel.Bindings!,
            Status = status,
            Window = new ReportWindow(from.ToUnixTimeMilliseconds(), to.ToUnixTimeMilliseconds()),
            Summary = summary,
            Sentence = summary,
        };

    private static string JoinSummaries(ReportSpec spec, IReadOnlyList<ReportPanelResult> panels)
    {
        if (panels.Count == 1 && spec.Title is null)
            return panels[0].Summary;

        var text = new StringBuilder();
        if (spec.Title is not null)
            text.Append(spec.Title).Append('\n');
        foreach (var panel in panels)
        {
            text.Append('\n').Append(CultureInfo.InvariantCulture, $"[{panel.Id}] {panel.Title ?? panel.Dataset}").Append('\n');
            text.Append(panel.Summary).Append('\n');
        }
        return text.ToString().Trim();
    }
}
