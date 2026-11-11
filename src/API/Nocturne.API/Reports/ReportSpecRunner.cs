using System.Globalization;
using System.Text;
using Nocturne.API.Reports.Derived;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

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

    /// <summary>What the run has read, so each dataset query is read once.</summary>
    public Datasets.ReportRunCache Cache { get; } = new();
}

/// <summary>
/// Runs a normalised <see cref="ReportSpec"/>, one panel and one layer after another: resolves each
/// range, checks the scopes of every dataset a layer or its sources read, reads each query once through
/// the run cache, folds a chart's layers onto its grid, derives the derived layers, colours them and
/// keeps the bound fields the token may read.
/// </summary>
/// <remarks>
/// Reads run in turn, not together: the datasets share the request's scoped services, which are not
/// made for concurrent use. The whole report has <see cref="Timeout"/>; a panel or a layer not read
/// by then comes back <see cref="ReportPanelStatus.TimedOut"/>, and a dataset that throws fails only
/// the panel or the layers that read it (<see cref="ReportPanelStatus.Failed"/>). The rest still draw,
/// and the panel's sentence names each layer that did not.
/// </remarks>
internal sealed class ReportSpecRunner(IReportDatasetRegistry registry, TimeProvider time, ILogger<ReportSpecRunner>? logger = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>How far back a history-clamped caller reads; the database clamps reads to the same window.</summary>
    public static readonly TimeSpan ClampedHistory = TimeSpan.FromHours(24);

    internal const string FailedNote = "Not shown: an error occurred when this panel was read. The other panels are not affected.";

    internal const string LimitedNote = "Limited to the last 24 hours: this token does not read older data.";

    internal const string OutsideHistoryNote = "Not shown: the range ends more than 24 hours ago, and this token reads only the last 24 hours.";

    /// <summary>The series hues a layer takes, in order, when an earlier layer on its track has its colour.</summary>
    internal static readonly IReadOnlyList<ChartColor> SeriesHues =
        [ChartColor.Profile, ChartColor.Chart3, ChartColor.Override, ChartColor.Chart4, ChartColor.Chart5];

    /// <summary>One run's state: the deadline, the instant ranges resolve against and the caller.</summary>
    private sealed class Run(ReportRunContext context, DateTimeOffset now, CancellationTokenSource deadline, CancellationToken token, CancellationToken caller)
    {
        public ReportRunContext Context { get; } = context;
        public DateTimeOffset Now { get; } = now;
        public CancellationTokenSource Deadline { get; } = deadline;
        public CancellationToken Token { get; } = token;
        public CancellationToken Caller { get; } = caller;
    }

    /// <summary>A read's output, or why there is none.</summary>
    private sealed record Read(ReportDatasetOutput? Output, ReportPanelStatus Status, string? Note);

    public async Task<ReportResult> RunAsync(ReportSpec spec, ReportRunContext context, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var run = new Run(context, time.GetUtcNow(), deadline, linked.Token, cancellationToken);

        var panels = new List<ReportPanelResult>(spec.Panels.Count);
        foreach (var panel in spec.Panels)
        {
            var result = panel.Kind == ReportPanelKind.Chart
                ? await RunChartAsync(panel, run)
                : await RunDatasetPanelAsync(panel, run);
            panels.Add(AtLimit(result, panel, run.Now));
        }

        var warnings = new List<string>();
        string Ids(IEnumerable<string> ids) => string.Join(", ", ids);
        var timedOut = panels.Where(p => p.Status == ReportPanelStatus.TimedOut).Select(p => p.Id)
            .Concat(LayerIds(panels, ReportPanelStatus.TimedOut)).ToList();
        if (timedOut.Count > 0)
            warnings.Add(
                $"{(timedOut.Count == 1 ? "Panel" : "Panels")} {Ids(timedOut)} did not finish in "
                + $"{Timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds and {(timedOut.Count == 1 ? "has" : "have")} no data. "
                + "Ask for a shorter range or fewer layers.");
        var failed = panels.Where(p => p.Status == ReportPanelStatus.Failed).Select(p => p.Id)
            .Concat(LayerIds(panels, ReportPanelStatus.Failed)).ToList();
        if (failed.Count > 0)
            warnings.Add($"{(failed.Count == 1 ? "Panel" : "Panels")} {Ids(failed)}: an error occurred when it was read. The rest of the report is not affected.");
        var limitedPanels = panels.Where(p => p.Limited).Select(p => p.Id).ToList();
        if (limitedPanels.Count > 0)
            warnings.Add($"{(limitedPanels.Count == 1 ? "Panel" : "Panels")} {Ids(limitedPanels)}: {LimitedNote}");

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

    /// <summary>"p1 (layer l2)" for each layer of a chart that partly drew with <paramref name="status"/>.</summary>
    private static IEnumerable<string> LayerIds(IEnumerable<ReportPanelResult> panels, ReportPanelStatus status) =>
        panels.Where(p => p.Status == ReportPanelStatus.Ok)
            .SelectMany(p => p.Tracks.SelectMany(t => t.Layers).Where(l => l.Status == status).Select(l => $"{p.Id} (layer {l.Id})"));

    /// <summary>
    /// The panel marked at the longest range its data covers, with the sentence that says how long that
    /// is, so a page offers no longer range than the panel draws.
    /// </summary>
    private ReportPanelResult AtLimit(ReportPanelResult result, ReportPanelSpec panel, DateTimeOffset now)
    {
        var max = Datasets(panel).Select(d => d.MaxRange).DefaultIfEmpty(TimeSpan.MaxValue).Min();
        var (from, to) = Resolve(panel.Range!, now);
        if (max == TimeSpan.MaxValue || to - from < max - TimeSpan.FromMinutes(1))
            return result;
        return result with { AtRangeLimit = true, RangeLimit = $"This panel covers at most {ReportSpecValidator.Describe(max)}." };
    }

    /// <summary>Every dataset a panel reads: its own, or each layer's and each source's.</summary>
    private IEnumerable<IReportDataset> Datasets(ReportPanelSpec panel)
    {
        IEnumerable<ReportDatasetParameters> Of(ReportDerivedSpec derived) =>
            derived.Sources.SelectMany(s => s.Dataset is { } d ? [d] : Of(s.Derived!));
        var parameters = panel.Dataset is { } own
            ? [own]
            : (panel.Tracks ?? []).SelectMany(t => t.Layers).SelectMany(l => l.Dataset is { } d ? [d] : Of(l.Derived!));
        return parameters.Select(p => registry.Find(p.DatasetName)!).Distinct();
    }

    internal static (DateTimeOffset From, DateTimeOffset To) Resolve(ReportRange range, DateTimeOffset now) =>
        range.Hours is { } hours ? (now.AddHours(-hours), now) : (range.From!.Value, range.To!.Value);

    /// <summary>The range a caller reads of <paramref name="range"/>: cut to the last 24 hours for a history-clamped one; null when none of it is left.</summary>
    private static (DateTimeOffset From, DateTimeOffset To, bool Limited)? Clamp(ReportRange range, Run run)
    {
        var (from, to) = Resolve(range, run.Now);
        if (!run.Context.HistoryClamped || from >= run.Now - ClampedHistory)
            return (from, to, false);
        from = run.Now - ClampedHistory;
        return to <= from ? null : (from, to, true);
    }

    private async Task<Read> ReadAsync(
        IReportDataset dataset,
        ReportDatasetParameters parameters,
        ReportRange range,
        DateTimeOffset from,
        DateTimeOffset to,
        ReportPanelKind kind,
        string? x,
        Run run,
        ReportReadShape? shape = null)
    {
        shape ??= ReportReadShape.Panel;
        if (!run.Context.HasScope(dataset.RequiredScope))
            return new Read(null, ReportPanelStatus.NotPermitted,
                $"Not shown: the token does not hold the '{dataset.RequiredScope}' scope that {dataset.Name} needs.");
        var context = run.Context;
        var key = ReportSpecValidator.ReadKey(dataset, parameters, range, kind, x, shape);
        if (run.Deadline.IsCancellationRequested && !context.Cache.Has(key))
            return new Read(null, ReportPanelStatus.TimedOut, "Not shown: the report ran out of time before this was read.");
        try
        {
            var output = await context.Cache.ReadAsync(key, () => dataset.RunAsync(
                new ReportDatasetRequest(parameters, kind, from, to, context.UseMmol, context.GrantedScopes, context.Services, context.ReadLog,
                    context.HistoryClamped, context.Cache, run.Now, x, context.Zone, shape.Fields, shape.Whole),
                run.Token));
            return new Read(output, ReportPanelStatus.Ok, null);
        }
        catch (OperationCanceledException) when (run.Deadline.IsCancellationRequested && !run.Caller.IsCancellationRequested)
        {
            return new Read(null, ReportPanelStatus.TimedOut, "Not shown: the report ran out of time before this was read.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "Report dataset {Dataset} failed", dataset.Name);
            return new Read(null, ReportPanelStatus.Failed, FailedNote);
        }
    }

    private async Task<ReportPanelResult> RunDatasetPanelAsync(ReportPanelSpec panel, Run run)
    {
        var dataset = registry.Find(panel.Dataset!.DatasetName)
            ?? throw new InvalidOperationException($"The validated spec names an unregistered dataset '{panel.Dataset.DatasetName}'.");
        if (Clamp(panel.Range!, run) is not { } clamped)
        {
            var (f, _) = Resolve(panel.Range!, run.Now);
            return EmptyPanel(panel, dataset.Name, f, f, ReportPanelStatus.NotPermitted, OutsideHistoryNote);
        }
        var (from, to, limited) = clamped;

        var read = await ReadAsync(dataset, panel.Dataset, panel.Range!, from, to, panel.Kind, null, run);
        if (read.Output is not { } output)
            return EmptyPanel(panel, dataset.Name, from, to, read.Status, read.Note!);

        var omitted = output.Omitted.Select(o => o.Field).ToHashSet(StringComparer.Ordinal);
        var catalogue = dataset.FieldsFor(panel.Kind);
        var useMmol = run.Context.UseMmol;
        var bound = panel.Bindings!.Columns ?? [];
        var colours = bound.Select(name => catalogue.Single(f => f.Name == name).ColorField).OfType<string>()
            .Where(name => catalogue.Any(f => f.Name == name));
        var companions = catalogue.Where(f => f.Companion).Select(f => f.Name);
        var fields = bound.Concat(colours).Concat(companions).Distinct(StringComparer.Ordinal)
            .Where(name => !omitted.Contains(name))
            .Select(name => Labelled(catalogue.Single(f => f.Name == name).ResolveUnit(useMmol), output))
            .ToList();
        var names = fields.Select(f => f.Name).ToList();

        var result = new ReportPanelResult
        {
            Id = panel.Id!,
            Title = panel.Title,
            Kind = panel.Kind,
            Dataset = dataset.Name,
            Bindings = panel.Bindings,
            Status = ReportPanelStatus.Ok,
            Window = output.Window ?? new ReportWindow(from.ToUnixTimeMilliseconds(), to.ToUnixTimeMilliseconds()),
            Fields = fields,
            Rows = [.. output.Rows.Select(row => (IReadOnlyDictionary<string, object?>)names.ToDictionary(n => n, n => row.GetValueOrDefault(n)))],
            Thresholds = output.Thresholds ?? (fields.Any(f => f.GlucoseScale) ? ReportThresholds.Of(null, useMmol) : null),
            Summary = output.Summary,
            Omitted = output.Omitted,
            Resolution = output.Resolution,
            Cohorts = output.Cohorts,
            Facts = Facts(output),
            Sentence = output.Sentence,
            Context = output.Context,
            LinkQuery = output.LinkQuery,
        };
        return limited ? result with { Summary = result.Summary + '\n' + LimitedNote, Limited = true } : result;
    }

    private static ReportField Labelled(ReportField field, ReportDatasetOutput output) =>
        output.FieldLabels?.GetValueOrDefault(field.Name) is { } label ? field with { Label = label } : field;

    private static IReadOnlyList<ReportFact> Facts(ReportDatasetOutput output) =>
        output.Facts.Count > ReportFacts.Max ? [.. output.Facts.Take(ReportFacts.Max)] : output.Facts;

    private async Task<ReportPanelResult> RunChartAsync(ReportPanelSpec panel, Run run)
    {
        var tracks = panel.Tracks!;
        var layers = tracks.SelectMany(t => t.Layers).ToList();
        var xKind = layers[0].XKind;
        if (Clamp(panel.Range!, run) is not { } clamped)
        {
            var (f, _) = Resolve(panel.Range!, run.Now);
            return EmptyPanel(panel, null, f, f, ReportPanelStatus.NotPermitted, OutsideHistoryNote) with { XKind = xKind, Grid = panel.Grid };
        }
        var (from, to, limited) = clamped;

        var trackResults = new List<ReportTrackResult>();
        var outputs = new List<ReportDatasetOutput>();
        var outputOf = new Dictionary<string, ReportDatasetOutput>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            var units = track.Layers.Where(l => l.HasAxis).Select(l => l.Unit).Distinct().ToList();
            var results = new List<ReportLayerResult>();
            foreach (var layer in track.Layers)
            {
                var (layerResult, output) = await RunLayerAsync(panel, layer, run);
                if (output is not null)
                {
                    outputs.Add(output);
                    outputOf[layer.Id!] = output;
                }
                var side = !layer.HasAxis ? ReportAxisSide.None : units.IndexOf(layer.Unit) == 0 ? ReportAxisSide.Left : ReportAxisSide.Right;
                results.Add(layerResult with { Side = side });
            }
            trackResults.Add(new ReportTrackResult(track.Id!, track.Title, track.Height ?? 1, Hue(results)));
        }

        var all = trackResults.SelectMany(t => t.Layers).ToList();
        var drawn = all.Where(l => l.Status == ReportPanelStatus.Ok).ToList();
        var notDrawn = all.Where(l => l.Status != ReportPanelStatus.Ok).ToList();
        var first = Lead(all);
        var leadOutput = first is null ? null : outputOf.GetValueOrDefault(first.Id);
        var sentence = first is { SettingsSentence: false } ? first.Sentence : null;
        var cutNotes = drawn.Where(l => l.CutNote is not null && l != first).Select(l => $"{l.Title}: {l.CutNote}").ToList();
        if (cutNotes.Count > 0)
            sentence = string.Join(' ', new[] { sentence }.OfType<string>().Concat(cutNotes));
        if (notDrawn.Count > 0)
            sentence = string.Join(' ', new[] { sentence }.OfType<string>().Append(
                $"Not shown: {string.Join("; ", notDrawn.Select(l => $"{l.Title} ({NotDrawnWords(l.Status)})"))}."));
        var summary = new StringBuilder();
        foreach (var layer in all)
            summary.Append(CultureInfo.InvariantCulture, $"[{layer.Title}]").Append('\n').Append(layer.Summary).Append('\n');

        var result = new ReportPanelResult
        {
            Id = panel.Id!,
            Title = panel.Title ?? DefaultPanelTitle(tracks.SelectMany(t => t.Layers).Single(l => l.Id == (first ?? all[0]).Id)),
            Kind = ReportPanelKind.Chart,
            Status = drawn.Count > 0 ? ReportPanelStatus.Ok : all[0].Status,
            Window = new ReportWindow(from.ToUnixTimeMilliseconds(), to.ToUnixTimeMilliseconds()),
            XKind = xKind,
            Grid = panel.Grid,
            Tracks = trackResults,
            Thresholds = outputs.Select(o => o.Thresholds).OfType<ReportThresholds>().FirstOrDefault()
                ?? (all.SelectMany(l => l.Fields).Any(f => f.GlucoseScale) ? ReportThresholds.Of(null, run.Context.UseMmol) : null),
            Summary = summary.ToString().TrimEnd(),
            Facts = first?.Facts.Where(f => !f.ComparesSettings).ToList() ?? [],
            Sentence = sentence,
            Context = leadOutput?.Context,
            Omitted = [.. drawn.SelectMany(l => l.Omitted).DistinctBy(o => o.Field)],
            Cohorts = drawn.SelectMany(l => l.Cohorts).DistinctBy(c => c.Number).ToList(),
            LinkQuery = leadOutput?.LinkQuery,
        };
        result = result with { Limited = drawn.Any(l => l.Limited) };
        return limited ? result with { Summary = result.Summary + '\n' + LimitedNote, Limited = true } : result;
    }

    /// <summary>
    /// The layer whose title, facts and sentence speak for the panel, chosen by what it draws, whatever its track's
    /// height: the first drawn glucose line, area or band; else the first drawn line, area, band or bars layer with a
    /// sentence that does not set the data against the settings; else the first drawn line, area, band, bars or points
    /// layer. A layer of spans or markers never leads.
    /// </summary>
    internal static ReportLayerResult? Lead(IReadOnlyList<ReportLayerResult> layers)
    {
        var drawn = layers.Where(l => l.Status == ReportPanelStatus.Ok && l.Mark is not (ReportMark.Spans or ReportMark.Markers)).ToList();
        var plotted = drawn.Where(l => l.Mark is ReportMark.Line or ReportMark.Area or ReportMark.Band or ReportMark.Bars).ToList();
        return plotted.FirstOrDefault(l => l.Mark != ReportMark.Bars && GlucoseScaled(l))
            ?? plotted.FirstOrDefault(l => l.Sentence is not null && !l.SettingsSentence)
            ?? drawn.FirstOrDefault();
    }

    private static bool GlucoseScaled(ReportLayerResult layer) =>
        (layer.Bindings.Y ?? []).Concat(layer.Bindings.Edges ?? [])
            .Any(n => layer.Fields.FirstOrDefault(f => f.Name == n) is { GlucoseScale: true });

    /// <summary>An untitled chart panel's title: its lead layer's title, else the name of that layer's dataset or op.</summary>
    private string DefaultPanelTitle(ReportLayerSpec lead) =>
        lead.Title ?? (lead.Derived is { } derived ? ReportDerivedOps.Of(derived).Label : registry.Find(lead.Dataset!.DatasetName)!.Label);

    /// <summary>
    /// The facts and the sentence of <paramref name="output"/> about fields <paramref name="drawn"/> names every one of
    /// (<see cref="ReportAbout"/>), and whether that sentence sets the data against the settings. A fact that names
    /// no fields is a table's or a tile's, and no chart layer prints it.
    /// </summary>
    internal static (IReadOnlyList<ReportFact> Facts, string? Sentence, bool SettingsSentence) About(ReportDatasetOutput output, IReadOnlyCollection<string> drawn)
    {
        var facts = output.Facts.Where(f => f.About is not null && ReportAbout.DrawnBy(f.About, drawn)).Take(ReportFacts.Max).ToList();
        if (output.SentenceAbout is null || ReportAbout.DrawnBy(output.SentenceAbout, drawn))
            return (facts, output.Sentence, output.SentenceComparesSettings && output.Sentence is not null);
        return (facts, output.OtherSentences.FirstOrDefault(s => ReportAbout.DrawnBy(s.About, drawn))?.Text, false);
    }

    private static string AggregateWords(ReportAggregate aggregate) => aggregate switch
    {
        ReportAggregate.Sum => "total",
        ReportAggregate.Mean => "mean",
        ReportAggregate.Max => "highest value",
        ReportAggregate.Last => "last value",
        _ => throw new ArgumentOutOfRangeException(nameof(aggregate), aggregate, null),
    };

    internal static string StepWords(int minutes) =>
        minutes % 1440 == 0 ? ReportFacts.Plural(minutes / 1440, "day")
        : minutes % 60 == 0 ? ReportFacts.Plural(minutes / 60, "hour")
        : ReportFacts.Plural(minutes, "minute");

    /// <summary>How each folded field went onto the grid: "Basal rate, last value per 1 day".</summary>
    internal static string FoldWords(IEnumerable<ReportField> folded, int step) =>
        string.Join("; ", folded.Select(f => $"{f.Label}, {AggregateWords(f.Aggregate!.Value)} per {StepWords(step)}"));

    private static string NotDrawnWords(ReportPanelStatus status) => status switch
    {
        ReportPanelStatus.NotPermitted => "your access does not include it",
        ReportPanelStatus.TimedOut => "the report ran out of time",
        _ => "an error occurred when it was read",
    };

    /// <summary>
    /// Gives each layer after the first on a track whose colour an earlier one takes a series hue: the
    /// colour of its first drawn field, a glucose-scale line counting as one colour.
    /// </summary>
    internal static IReadOnlyList<ReportLayerResult> Hue(IReadOnlyList<ReportLayerResult> layers)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var hued = new List<ReportLayerResult>(layers.Count);
        foreach (var layer in layers)
        {
            if (layer.Mark is ReportMark.Markers or ReportMark.Spans || layer.Fields.Count == 0)
            {
                hued.Add(layer);
                continue;
            }
            var lead = (layer.Bindings.Y ?? []).Concat(layer.Bindings.Edges ?? [])
                .Select(name => layer.Fields.FirstOrDefault(f => f.Name == name)).OfType<ReportField>().FirstOrDefault();
            var key = layer.Color is { } own ? ReportWire.Name(own)
                : lead is null ? "foreground" : lead.GlucoseScale ? "glucose" : lead.Color is { } c ? ReportWire.Name(c) : "foreground";
            if (used.Add(key))
            {
                hued.Add(layer);
                continue;
            }
            var hue = SeriesHues.FirstOrDefault(h => !used.Contains(ReportWire.Name(h)), SeriesHues[used.Count % SeriesHues.Count]);
            used.Add(ReportWire.Name(hue));
            hued.Add(layer with { Color = hue });
        }
        return hued;
    }

    private async Task<(ReportLayerResult Result, ReportDatasetOutput? Output)> RunLayerAsync(ReportPanelSpec panel, ReportLayerSpec layer, Run run)
    {
        var range = layer.Range ?? panel.Range!;
        var title = layer.Title ?? DefaultTitle(layer, run.Context.UseMmol);
        ReportLayerResult Empty(ReportPanelStatus status, string note, DateTimeOffset from, DateTimeOffset to) => new()
        {
            Id = layer.Id!,
            Title = title,
            Mark = layer.Mark!.Value,
            Bindings = layer.Bindings!,
            XKind = layer.XKind,
            Unit = layer.Unit,
            Side = ReportAxisSide.None,
            Status = status,
            Window = new ReportWindow(from.ToUnixTimeMilliseconds(), to.ToUnixTimeMilliseconds()),
            Sentence = note,
            Summary = note,
        };

        if (Clamp(range, run) is not { } clamped)
        {
            var (f, _) = Resolve(range, run.Now);
            return (Empty(ReportPanelStatus.NotPermitted, OutsideHistoryNote, f, f), null);
        }
        var (from, to, _) = clamped;
        var fromMs = from.ToUnixTimeMilliseconds();
        var toMs = to.ToUnixTimeMilliseconds();

        if (layer.Derived is { } derived)
        {
            if (MissingScopes(derived, run.Context) is { Count: > 0 } missing)
                return (Empty(ReportPanelStatus.NotPermitted,
                    $"Not shown: a source of this layer needs the {string.Join(" and ", missing.Select(ScopeWords))} permission, which this token does not hold.",
                    from, to), null);
            var computed = await DeriveAsync(derived, panel, layer.XKind, range, from, to, run);
            if (computed.Status != ReportPanelStatus.Ok)
                return (Empty(computed.Status, computed.Note!, from, to), null);
            var op = ReportDerivedOps.Of(derived);
            var outputFields = computed.Fields!;
            var bound = BoundNames(layer.Bindings!);
            var cut = computed.Cuts.Count > 0 ? string.Join(' ', computed.Cuts.Distinct()) : null;
            return (new ReportLayerResult
            {
                Id = layer.Id!,
                Title = title,
                Mark = layer.Mark!.Value,
                Bindings = layer.Bindings!,
                XKind = layer.XKind,
                Unit = layer.Unit,
                Side = ReportAxisSide.None,
                Status = ReportPanelStatus.Ok,
                Window = new ReportWindow(fromMs, toMs),
                Fields = [.. outputFields.Where(f => bound.Contains(f.Name))],
                Rows = [.. computed.Output!.Rows.Select(row => (IReadOnlyDictionary<string, object?>)bound.ToDictionary(n => n, n => row.GetValueOrDefault(n)))],
                Facts = computed.Output.Facts.Take(ReportFacts.Max).ToList(),
                Sentence = cut is null ? computed.Output.Sentence : $"{computed.Output.Sentence} {cut}",
                Summary = cut is null
                    ? $"{op.Label}, on a grid of {computed.Step} minutes. {computed.Output.Summary}"
                    : $"{op.Label}, on a grid of {computed.Step} minutes. {computed.Output.Summary}\n{cut}",
                Limited = cut is not null,
                CutNote = cut,
                SourceCohorts = computed.Cohorts,
                Spans = layer.Mark == ReportMark.Band
                    ? ReportBandSpan.Of([.. (layer.Bindings!.Edges ?? []).Select(n => outputFields.Single(f => f.Name == n))])
                    : [],
            }, null);
        }

        var dataset = registry.Find(layer.Dataset!.DatasetName)!;
        var bindings = layer.Bindings!;
        var valueNames = ValueNames(bindings);
        var read = await ReadAsync(dataset, layer.Dataset, range, from, to, ReportPanelKind.Chart, bindings.X, run, LayerShape(bindings));
        if (read.Output is not { } output)
            return (Empty(read.Status, read.Note!, from, to), null);

        var useMmol = run.Context.UseMmol;
        var omitted = output.Omitted.Select(o => o.Field).ToHashSet(StringComparer.Ordinal);
        var catalogue = dataset.FieldsFor(ReportPanelKind.Chart);
        var lost = valueNames.Where(omitted.Contains).ToList();
        if (valueNames.Count > 0 && lost.Count == valueNames.Count)
        {
            var scopes = output.Omitted.Where(o => lost.Contains(o.Field)).Select(o => o.Scope).Distinct();
            return (Empty(ReportPanelStatus.NotPermitted,
                $"Not shown: this layer needs the {string.Join(" and ", scopes.Select(ScopeWords))} permission, which this token does not hold.",
                from, to), output);
        }

        var boundNames = layer.Mark == ReportMark.Spans
            ? [.. catalogue.Where(f => !f.Companion).Select(f => f.Name)]
            : BoundNames(bindings);
        var colours = boundNames.Select(n => catalogue.FirstOrDefault(f => f.Name == n)?.ColorField).OfType<string>();
        var drawnNames = layer.Mark == ReportMark.Spans ? boundNames : valueNames;
        var companions = catalogue.Where(f => f.Companion).Select(f => f.Name)
            .Where(n => n != ReportField.SentenceField || dataset.RowSentenceAbout is not { } about || ReportAbout.DrawnBy(about, drawnNames));
        var fields = boundNames.Concat(colours).Concat(companions).Distinct(StringComparer.Ordinal)
            .Where(n => !omitted.Contains(n))
            .Select(n => Labelled(catalogue.Single(f => f.Name == n).ResolveUnit(useMmol), output))
            .ToList();
        var names = fields.Select(f => f.Name).ToList();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = [.. output.Rows.Select(row =>
            (IReadOnlyDictionary<string, object?>)names.ToDictionary(n => n, n => row.GetValueOrDefault(n)))];

        var folded = panel.Grid is > 0 && ReportGrid.Applies(layer.XKind) && layer.Mark != ReportMark.Spans;
        var step = folded ? ReportGrid.Step(layer.XKind, panel.Grid!.Value, from, to) : 0;
        string? context = null;
        if (folded)
        {
            var foldFields = fields.Where(f => valueNames.Contains(f.Name)).ToList();
            context = FoldWords(foldFields, step);
            var colourFields = fields.Select(f => f.ColorField).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var carried = fields
                .Where(f => (f.Companion && f.Name != ReportField.SentenceField) || colourFields.Contains(f.Name))
                .Select(f => f.Name).Where(n => n != bindings.Series).ToList();
            rows = ReportGrid.FoldRows(rows, bindings.X!, foldFields, bindings.Series, carried, layer.XKind, step, fromMs, toMs, run.Context.Zone);
            fields = [.. fields.Where(f => f.Name != ReportField.SentenceField)];
        }

        var markers = layer.Mark == ReportMark.Markers
            ? Markers(folded ? rows : output.Rows, bindings, fields)
            : [];
        if (layer.Title is null && layer.Mark == ReportMark.Markers)
            title = MarkersTitle(dataset.Label, fields, bindings, markers);
        var (facts, sentence, settingsSentence) = About(output, drawnNames);
        if (output.CutNote is { } cutNote)
            sentence = sentence is null ? cutNote : $"{sentence} {cutNote}";

        return (new ReportLayerResult
        {
            Id = layer.Id!,
            Title = title,
            Mark = layer.Mark!.Value,
            Bindings = bindings,
            XKind = layer.XKind,
            Unit = layer.Unit,
            Side = ReportAxisSide.None,
            Status = ReportPanelStatus.Ok,
            Window = output.Window ?? new ReportWindow(fromMs, toMs),
            Fields = fields,
            Rows = rows,
            Facts = facts,
            Sentence = sentence,
            SettingsSentence = settingsSentence,
            Context = context,
            Summary = folded ? $"{output.Summary}\nFolded onto a grid of {step} minutes." : output.Summary,
            Limited = output.CutNote is not null,
            CutNote = output.CutNote,
            Spans = layer.Mark == ReportMark.Band
                ? ReportBandSpan.Of([.. (bindings.Edges ?? []).Select(n => fields.FirstOrDefault(f => f.Name == n)).OfType<ReportField>()])
                : [],
            Markers = markers,
            Cohorts = output.Cohorts,
            Omitted = output.Omitted,
            Color = output.Color,
            Resolution = output.Resolution,
        }, output);
    }

    /// <summary>How a layer drawn with <paramref name="bindings"/> reads its dataset: the value fields it draws.</summary>
    internal static ReportReadShape LayerShape(ReportLayerBindings bindings)
    {
        var values = ValueNames(bindings);
        return new ReportReadShape(values.Count > 0 ? values : null);
    }

    /// <summary>How a derived source reads its dataset: its one field, every row of the range.</summary>
    internal static ReportReadShape SourceShape(ReportSourceSpec source) => new([source.Field], Whole: true);

    /// <summary>The fields whose values a layer draws: its lines, edges and fit.</summary>
    internal static IReadOnlyList<string> ValueNames(ReportLayerBindings bindings) =>
        [.. (bindings.Y ?? []).Concat(bindings.Edges ?? []).Concat(new[] { bindings.Fit }.OfType<string>())];

    /// <summary>An untitled markers layer's name in the legend: the dataset's, and the fields it drew a marker of, if any.</summary>
    internal static string MarkersTitle(string datasetLabel, IReadOnlyList<ReportField> fields, ReportLayerBindings bindings, IReadOnlyList<ReportMarker> markers)
    {
        var kinds = markers.Select(m => m.Kind).ToHashSet();
        var shown = fields.Where(f => (bindings.Y ?? []).Contains(f.Name) && f.Marker is { } k && kinds.Contains(k)).Select(f => f.Label).ToList();
        return shown.Count == 0 ? datasetLabel : $"{datasetLabel} · {string.Join(", ", shown)}";
    }

    /// <summary>The field names a layer's bindings draw, in drawing order.</summary>
    internal static IReadOnlyList<string> BoundNames(ReportLayerBindings bindings) =>
        [.. new[] { bindings.X, bindings.Start, bindings.End, bindings.Category }.OfType<string>()
            .Concat(bindings.Y ?? [])
            .Concat(bindings.Edges ?? [])
            .Concat(new[] { bindings.Fit, bindings.Series }.OfType<string>())
            .Distinct(StringComparer.Ordinal)];

    /// <summary>The markers a markers layer draws: one per row and marker field with an amount, outlined where the algorithm gave it.</summary>
    internal static IReadOnlyList<ReportMarker> Markers(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows, ReportLayerBindings bindings, IReadOnlyList<ReportField> fields)
    {
        static double? Number(object? value) => value switch
        {
            double d => d,
            int i => i,
            long l => l,
            _ => null,
        };
        var marked = (bindings.Y ?? []).Select(n => fields.FirstOrDefault(f => f.Name == n)).OfType<ReportField>().Where(f => f.Marker is not null).ToList();
        var markers = new List<ReportMarker>();
        foreach (var row in rows)
        {
            if (Number(row.GetValueOrDefault(bindings.X!)) is not { } at)
                continue;
            foreach (var field in marked)
                if (Number(row.GetValueOrDefault(field.Name)) is { } amount and > 0)
                    markers.Add(new ReportMarker((long)at, field.Marker!.Value, Math.Round(amount, 2),
                        Equals(row.GetValueOrDefault(ReportField.AutomaticField), "yes")));
        }
        return [.. markers.OrderBy(m => m.Time)];
    }

    /// <summary>What a derived series computed, or why it did not.</summary>
    /// <param name="Cuts">Sentences saying where a source's rows were cut, so the series starts later than the range.</param>
    /// <param name="Cohorts">The event groups each source of grouped data offers, by the source's key (a, b), with the API's words.</param>
    private sealed record Derivation(
        ReportPanelStatus Status,
        string? Note,
        ReportDerivedOutput? Output,
        IReadOnlyList<ReportField>? Fields,
        ReportSourceInfo? Info,
        int Step,
        IReadOnlyList<string> Cuts,
        IReadOnlyDictionary<string, IReadOnlyList<ReportCohort>> Cohorts)
    {
        public static Derivation Failed(ReportPanelStatus status, string? note) => new(status, note, null, null, null, 0, [], new Dictionary<string, IReadOnlyList<ReportCohort>>());
    }

    /// <summary>The scopes the sources of <paramref name="derived"/> need that the caller does not hold: each dataset's and each source field's.</summary>
    private List<string> MissingScopes(ReportDerivedSpec derived, ReportRunContext context)
    {
        var missing = new List<string>();
        foreach (var source in derived.Sources)
        {
            if (source.Derived is { } inner)
            {
                missing.AddRange(MissingScopes(inner, context));
                continue;
            }
            var dataset = registry.Find(source.Dataset!.DatasetName)!;
            foreach (var scope in new[] { dataset.RequiredScope, dataset.Fields.FirstOrDefault(f => f.Name == source.Field)?.Scope }.OfType<string>())
                if (!context.HasScope(scope))
                    missing.Add(scope);
        }
        return [.. missing.Distinct()];
    }

    private async Task<Derivation> DeriveAsync(
        ReportDerivedSpec derived, ReportPanelSpec panel, ReportXKind outputKind, ReportRange range, DateTimeOffset from, DateTimeOffset to, Run run)
    {
        var op = ReportDerivedOps.Of(derived);
        var fromMs = from.ToUnixTimeMilliseconds();
        var toMs = to.ToUnixTimeMilliseconds();
        var series = new List<GridSeries>();
        var infos = new List<ReportSourceInfo>();
        var cuts = new List<string>();
        var cohorts = new Dictionary<string, IReadOnlyList<ReportCohort>>(StringComparer.Ordinal);
        var zone = run.Context.Zone;
        ReportXKind? sourceKind = null;
        var step = 0;
        foreach (var (source, key) in derived.Sources.Zip(new[] { "a", "b" }))
        {
            if (source.Derived is { } inner)
            {
                var nested = await DeriveAsync(inner, panel, outputKind, range, from, to, run);
                if (nested.Status != ReportPanelStatus.Ok)
                    return nested;
                cuts.AddRange(nested.Cuts);
                var field = nested.Fields!.Single(f => f.Name == source.Field);
                var kind = ReportDerivedOps.Of(inner).OutputXKind(nested.Info!.XKind);
                sourceKind = kind;
                step = nested.Step;
                series.Add(new GridSeries(nested.Output!.Rows.Where(r => r.GetValueOrDefault(ReportDerivedOps.SlotField) is long)
                    .ToDictionary(r => (long)r[ReportDerivedOps.SlotField]!, r => r.GetValueOrDefault(field.Name) as double?)));
                infos.Add(new ReportSourceInfo(field, kind, field.Label));
                continue;
            }

            var dataset = registry.Find(source.Dataset!.DatasetName)!;
            var catalogue = dataset.FieldsFor(ReportPanelKind.Chart);
            var axis = catalogue.First(f => !f.Companion && f.AxisKind is { } k && ReportDerivedOps.GridKinds.Contains(k));
            var read = await ReadAsync(dataset, source.Dataset, range, from, to, ReportPanelKind.Chart, axis.Name, run, SourceShape(source));
            if (read.Output is not { } output)
                return Derivation.Failed(read.Status, read.Note);
            if (output.Omitted.FirstOrDefault(o => o.Field == source.Field) is { } withheld)
                return Derivation.Failed(ReportPanelStatus.NotPermitted,
                    $"Not shown: a source of this layer needs the {ScopeWords(withheld.Scope)} permission, which this token does not hold.");
            var sourceField = catalogue.Single(f => f.Name == source.Field).ResolveUnit(run.Context.UseMmol);
            sourceKind = axis.AxisKind!.Value;
            step = ReportGrid.Step(sourceKind.Value, panel.Grid is > 0 ? panel.Grid.Value : ReportGrid.DefaultMinutes, from, to);
            var window = output.Window ?? new ReportWindow(fromMs, toMs);
            var zeroFrom = Math.Max(window.From, output.CompleteFrom ?? window.From);
            if (output.CompleteFrom is not null && output.CutNote is { } cutNote)
                cuts.Add(cutNote);
            if (dataset.SeriesField is not null && output.Cohorts.Count > 0)
                cohorts[key] = output.Cohorts;
            series.Add(new GridSeries(
                ReportGrid.Values([.. dataset.SeriesRows(output.Rows, source.Series ?? 1)], axis.Name, sourceField, sourceKind.Value, step, fromMs, toMs, zone),
                sourceField.Step && sourceField.Aggregate == ReportAggregate.Last,
                sourceField.Aggregate == ReportAggregate.Sum ? (zeroFrom, window.To) : null));
            infos.Add(new ReportSourceInfo(sourceField, sourceKind.Value, sourceField.Label));
        }

        var (keys, aligned) = ReportGrid.Align(sourceKind!.Value, step, fromMs, toMs, series, zone);
        var input = new ReportDerivedInput(keys, aligned[0], aligned.Length > 1 ? aligned[1] : null, step, sourceKind.Value, infos[0], infos.ElementAtOrDefault(1));
        var computed = op.Compute(derived, input);
        return new Derivation(ReportPanelStatus.Ok, null, computed, op.OutputFields(derived, infos[0], infos.ElementAtOrDefault(1)), infos[0], step, cuts, cohorts);
    }

    /// <summary>A layer's name in the legend when it gives none: the dataset's and the fields' names, or the op's and the sources'.</summary>
    private string DefaultTitle(ReportLayerSpec layer, bool useMmol)
    {
        if (layer.Derived is { } derived)
        {
            var op = ReportDerivedOps.Of(derived);
            return $"{op.Label} · {string.Join(" and ", derived.Sources.Select(SourceWords))}";
        }
        var dataset = registry.Find(layer.Dataset!.DatasetName)!;
        var fields = dataset.FieldsFor(ReportPanelKind.Chart);
        var named = (layer.Bindings!.Y ?? []).Concat(layer.Mark == ReportMark.Band && layer.Bindings.Y is null ? layer.Bindings.Edges ?? [] : [])
            .Select(n => fields.FirstOrDefault(f => f.Name == n)?.Label).OfType<string>().ToList();
        return named.Count == 0 || layer.Mark == ReportMark.Spans ? dataset.Label : $"{dataset.Label} · {string.Join(", ", named)}";
    }

    private string SourceWords(ReportSourceSpec source) =>
        source.Derived is { } inner
            ? $"{ReportDerivedOps.Of(inner).Label.ToLowerInvariant()} of {string.Join(" and ", inner.Sources.Select(SourceWords))}"
            : registry.Find(source.Dataset!.DatasetName)!.Fields.FirstOrDefault(f => f.Name == source.Field)?.Label ?? source.Field;

    private static string ScopeWords(string scope) => scope switch
    {
        Scope.GlucoseRead => "glucose",
        _ => ReportNeedsScopeAttribute.Noun(scope),
    };

    private static ReportPanelResult EmptyPanel(
        ReportPanelSpec panel,
        string? dataset,
        DateTimeOffset from,
        DateTimeOffset to,
        ReportPanelStatus status,
        string summary) =>
        new()
        {
            Id = panel.Id!,
            Title = panel.Title,
            Kind = panel.Kind,
            Dataset = dataset,
            Bindings = panel.Bindings,
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
            text.Append('\n').Append(CultureInfo.InvariantCulture, $"[{panel.Id}] {panel.Title ?? panel.Dataset ?? "Chart"}").Append('\n');
            text.Append(panel.Summary).Append('\n');
        }
        return text.ToString().TrimEnd();
    }
}
