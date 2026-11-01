using System.Text.Json;
using System.Text.Json.Serialization;
using Nocturne.API.Reports.Derived;

namespace Nocturne.API.Reports;

internal sealed partial class ReportSpecValidator
{
    private static readonly string[] SpecKeys = ["version", "title", "range", "panels"];
    private static readonly string[] ChartPanelKeys = ["id", "title", "kind", "range", "grid", "tracks"];
    private static readonly string[] DatasetPanelKeys = ["id", "title", "kind", "range", "dataset", "bindings"];
    private static readonly string[] TrackKeys = ["id", "title", "height", "layers"];
    private static readonly string[] LayerKeys = ["id", "title", "dataset", "derived", "mark", "bindings", "range"];
    private static readonly string[] SourceKeys = ["dataset", "derived", "field", "series"];
    private static readonly string[] ColumnKeys = ["columns"];
    private static readonly string[] LayerBindingKeys = ["x", "y", "edges", "series", "fit", "stacked", "orientation", "start", "end", "category"];

    private static readonly JsonSerializerOptions KeyOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// The key one read is cached and counted under: the dataset query, the range as the spec gives it,
    /// the kind of panel and the field it is laid along. Two layers or sources with the same key read once.
    /// </summary>
    internal static string ReadKey(ReportDatasetParameters parameters, ReportRange range, ReportPanelKind kind, string? x) =>
        $"{parameters.DatasetName}|{JsonSerializer.Serialize(parameters, parameters.GetType(), KeyOptions)}|{JsonSerializer.Serialize(range, KeyOptions)}|{ReportWire.Name(kind)}|{x}";

    /// <summary>One dataset read a layer makes: for the layer itself, or for one of its sources.</summary>
    private sealed record LayerRead(IReportDataset Dataset, ReportDatasetParameters Parameters, string? X);

    /// <summary>A layer as read, with what the panel's checks need beyond its spec.</summary>
    /// <param name="Folded">The fields the panel's grid folds, for a layer of a dataset.</param>
    /// <param name="Paired">Whether it derives from two sources, which needs a grid.</param>
    private sealed record ParsedLayer(ReportLayerSpec Spec, string At, IReadOnlyList<LayerRead> Reads, IReadOnlyList<ReportField> Folded, bool Paired);

    /// <summary>A derived series as read.</summary>
    private sealed record ParsedDerived(
        ReportDerivedSpec Spec,
        IReportDerivedOp Op,
        ReportXKind XKind,
        IReadOnlyList<ReportField> Fields,
        IReadOnlyList<LayerRead> Reads);

    public ReportSpecValidation Validate(JsonElement spec, bool useMmol = false)
    {
        var errors = new List<ReportSpecError>();
        void Error(string path, string message) => errors.Add(new ReportSpecError(path, message));
        var now = time.GetUtcNow();

        if (spec.ValueKind != JsonValueKind.Object)
        {
            Error("spec", "Send the spec as a JSON object with 'version' and 'panels'.");
            return new ReportSpecValidation(null, errors);
        }

        UnknownKeys(spec, "spec", SpecKeys, Error);

        if (!spec.TryGetProperty("version", out var version) || version.ValueKind == JsonValueKind.Null)
            Error("version", $"Missing. Send \"version\": {ReportSpec.CurrentVersion}.");
        else if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != ReportSpec.CurrentVersion)
            Error("version", $"{Shorten(version.GetRawText())} is not a supported version. Send the number {ReportSpec.CurrentVersion}.");

        var title = OptionalString(spec, "title", "title", ReportSpec.MaxTitleLength, Error);
        var reportRange = spec.TryGetProperty("range", out var r) ? ReadRange(r, "range", now, Error) : null;

        var panels = new List<ReportPanelSpec>();
        var reads = new Dictionary<string, IReportDataset>(StringComparer.Ordinal);
        if (!spec.TryGetProperty("panels", out var panelsElement) || panelsElement.ValueKind != JsonValueKind.Array)
        {
            Error("panels", $"Missing. Send a list of 1 to {ReportSpec.MaxPanels} panels.");
        }
        else if (panelsElement.GetArrayLength() is 0 or > ReportSpec.MaxPanels)
        {
            Error("panels", $"A report has 1 to {ReportSpec.MaxPanels} panels; this one has {panelsElement.GetArrayLength()}. "
                + "Split it into more than one report.");
        }
        else
        {
            var index = 0;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var panel in panelsElement.EnumerateArray())
            {
                if (ReadPanel(panel, index, reportRange, ids, now, useMmol, reads, Error) is { } read)
                    panels.Add(read);
                index++;
            }

            if (reads.Count > ReportSpec.MaxReads)
                Error("panels",
                    $"This report reads {reads.Count} datasets; a report reads at most {ReportSpec.MaxReads}. Layers and sources that read "
                    + "the same data with the same settings and range count once. Remove layers, or split the report.");

            foreach (var group in reads.Values.GroupBy(d => d.Name))
            {
                var heavy = group.First();
                if (group.Count() > heavy.MaxPanelsPerReport)
                    Error("panels", $"A report reads {heavy.Label} at most {heavy.MaxPanelsPerReport} times; this one reads it {group.Count()} times. "
                        + "Split them into more than one report.");
            }

            for (var i = 0; i < panels.Count; i++)
            {
                if (panels[i].Id is null)
                {
                    var assigned = FreeId(ids, "p", i);
                    ids.Add(assigned);
                    panels[i] = panels[i] with { Id = assigned };
                }
            }
        }

        if (errors.Count > 0)
            return new ReportSpecValidation(null, errors);

        return new ReportSpecValidation(
            new ReportSpec { Version = ReportSpec.CurrentVersion, Title = title, Range = reportRange, Panels = panels },
            errors);
    }

    private ReportPanelSpec? ReadPanel(
        JsonElement panel,
        int index,
        ReportRange? reportRange,
        HashSet<string> ids,
        DateTimeOffset now,
        bool useMmol,
        Dictionary<string, IReportDataset> reads,
        Action<string, string> error)
    {
        var at = $"panels[{index}]";
        if (panel.ValueKind != JsonValueKind.Object)
        {
            error(at, "Send each panel as an object with 'kind'.");
            return null;
        }

        var failed = false;
        void Error(string path, string message)
        {
            failed = true;
            error(path, message);
        }

        ReportPanelKind? kind = null;
        if (!panel.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String)
            Error($"{at}.kind", $"Missing. Send one of: {ReportWire.List(Enum.GetValues<ReportPanelKind>())}.");
        else if (ReportWire.Parse<ReportPanelKind>(k.GetString()) is not { } parsed)
            Error($"{at}.kind", $"'{Shorten(k.GetString()!)}' is not a kind of panel. Send one of: {ReportWire.List(Enum.GetValues<ReportPanelKind>())}.");
        else
            kind = parsed;

        UnknownKeys(panel, at, kind switch
        {
            ReportPanelKind.Chart => ChartPanelKeys,
            null => [.. ChartPanelKeys.Union(DatasetPanelKeys)],
            _ => DatasetPanelKeys,
        }, (path, message) =>
        {
            var key = path[(at.Length + 1)..];
            // A normalised spec writes the other kind's keys as null.
            if (panel.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Null && ChartPanelKeys.Union(DatasetPanelKeys).Contains(key))
                return;
            Error(path, kind == ReportPanelKind.Chart && DatasetPanelKeys.Contains(key)
                ? $"A chart panel names its data in its layers. Remove '{key}'."
                : kind is not null and not ReportPanelKind.Chart && ChartPanelKeys.Contains(key)
                    ? $"Only a chart panel takes '{key}'. Remove it."
                    : message);
        });

        var id = OptionalString(panel, "id", $"{at}.id", ReportSpec.MaxIdLength, Error);
        if (id is not null && !IdPattern().IsMatch(id))
            Error($"{at}.id", "Use only letters, digits, '-' and '_'.");
        if (id is not null && !ids.Add(id))
            Error($"{at}.id", $"'{id}' is already the id of another panel. Panel ids must be unique.");
        var title = OptionalString(panel, "title", $"{at}.title", ReportSpec.MaxTitleLength, Error);
        var range = (panel.TryGetProperty("range", out var pr) ? ReadRange(pr, $"{at}.range", now, Error) : null) ?? reportRange;

        if (kind is not { } panelKind)
            return null;

        var read = panelKind == ReportPanelKind.Chart
            ? ReadChart(panel, at, range, now, useMmol, reads, Error)
            : ReadDatasetPanel(panel, at, panelKind, range, useMmol, reads, Error);
        if (failed || read is null)
            return null;
        return read with { Id = id, Title = title };
    }

    private ReportPanelSpec? ReadDatasetPanel(
        JsonElement panel,
        string at,
        ReportPanelKind kind,
        ReportRange? range,
        bool useMmol,
        Dictionary<string, IReportDataset> reads,
        Action<string, string> error)
    {
        var dataset = ReadDataset(panel, at, error, out var parameters);
        if (dataset is null)
            return null;
        if (!dataset.PanelKinds.Contains(kind))
        {
            var ways = dataset.PanelKinds.Select(PanelKindWords).ToList();
            if (dataset.Marks.Count > 0)
                ways.Insert(0, "a chart");
            error($"{at}.kind", $"{dataset.Label} cannot be shown as {PanelKindWords(kind)}. Show it as {string.Join(" or ", ways)}.");
            return null;
        }

        range ??= new ReportRange { Hours = (int)dataset.DefaultRange.TotalHours };
        CheckSpan(dataset, range, $"{at}.range", error);

        var fields = dataset.FieldsFor(kind).Where(f => !f.Companion).ToList();
        var columns = ReadColumns(panel, $"{at}.bindings", dataset, fields, error);
        if (parameters is null || columns is null)
            return null;
        reads.TryAdd(ReadKey(parameters, range, kind, null), dataset);
        return new ReportPanelSpec
        {
            Kind = kind,
            Range = range,
            Dataset = parameters,
            Bindings = new ReportBindings { Columns = columns },
        };
    }

    private static IReadOnlyList<string>? ReadColumns(JsonElement panel, string at, IReportDataset dataset, List<ReportField> fields, Action<string, string> error)
    {
        panel.TryGetProperty("bindings", out var element);
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return [.. fields.Select(f => f.Name)];
        if (element.ValueKind != JsonValueKind.Object)
        {
            error(at, "Send the bindings as an object.");
            return null;
        }
        UnknownKeys(element, at, ColumnKeys, error);
        if (!element.TryGetProperty("columns", out var value) || value.ValueKind == JsonValueKind.Null)
            return [.. fields.Select(f => f.Name)];
        if (value.ValueKind != JsonValueKind.Array || !value.EnumerateArray().All(c => c.ValueKind == JsonValueKind.String))
        {
            error($"{at}.columns", "Send a list of field names, for example [\"glucose\"].");
            return null;
        }
        var columns = value.EnumerateArray().Select(c => c.GetString()!).ToList();
        if (columns.Count == 0)
            error($"{at}.columns", "Send at least one field, or leave 'columns' out for every field.");
        if (columns.Distinct().Count() != columns.Count)
            error($"{at}.columns", "A field is named twice.");
        var names = string.Join(", ", fields.Select(f => f.Name));
        foreach (var name in columns.Where(n => fields.All(f => f.Name != n)))
            error($"{at}.columns", dataset.FieldsFor(ReportPanelKind.Table).Any(f => f.Name == name && f.Companion)
                ? $"'{name}' comes with every row and cannot be shown on its own. Fields: {names}."
                : $"'{Shorten(name)}' is not a field of {dataset.Name}. Fields: {names}.");
        return columns;
    }

    private ReportPanelSpec? ReadChart(
        JsonElement panel,
        string at,
        ReportRange? range,
        DateTimeOffset now,
        bool useMmol,
        Dictionary<string, IReportDataset> reads,
        Action<string, string> error)
    {
        var failed = false;
        void Error(string path, string message)
        {
            failed = true;
            error(path, message);
        }

        int? grid = null;
        var gridSent = panel.TryGetProperty("grid", out var g) && g.ValueKind != JsonValueKind.Null;
        if (gridSent)
        {
            if (g.ValueKind == JsonValueKind.Number && g.TryGetInt32(out var minutes) && minutes is >= 0 and <= ReportGrid.MaxMinutes)
                grid = minutes;
            else
                Error($"{at}.grid", $"Send a whole number of minutes, 0 to {ReportGrid.MaxMinutes}: 0 for every reading.");
        }

        if (!panel.TryGetProperty("tracks", out var tracksElement) || tracksElement.ValueKind != JsonValueKind.Array
            || tracksElement.GetArrayLength() is 0 or > ReportSpec.MaxTracks)
        {
            Error($"{at}.tracks", tracksElement.ValueKind == JsonValueKind.Array
                ? $"A chart has 1 to {ReportSpec.MaxTracks} tracks; this one has {tracksElement.GetArrayLength()}."
                : $"Missing. Send a list of 1 to {ReportSpec.MaxTracks} tracks, each with its layers.");
            return null;
        }

        var tracks = new List<(ReportTrackSpec Spec, List<ParsedLayer> Layers, string At)>();
        var trackIds = new HashSet<string>(StringComparer.Ordinal);
        var layerIds = new HashSet<string>(StringComparer.Ordinal);
        var t = 0;
        foreach (var track in tracksElement.EnumerateArray())
        {
            if (ReadTrack(track, $"{at}.tracks[{t}]", now, useMmol, trackIds, layerIds, Error) is { } read)
                tracks.Add(read);
            t++;
        }

        var layers = tracks.SelectMany(x => x.Layers).ToList();
        var layerCount = tracksElement.EnumerateArray()
            .Sum(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("layers", out var l) && l.ValueKind == JsonValueKind.Array ? l.GetArrayLength() : 0);
        if (layerCount > ReportSpec.MaxLayers)
            Error(at, $"A chart has at most {ReportSpec.MaxLayers} layers; this one has {layerCount}. Move some to another panel.");
        if (failed || layers.Count == 0)
            return null;

        range ??= new ReportRange { Hours = (int)Math.Max(1, layers[0].Reads[0].Dataset.DefaultRange.TotalHours) };

        var xKind = layers[0].Spec.XKind;
        foreach (var layer in layers.Skip(1).Where(l => l.Spec.XKind != xKind))
            Error($"{layer.At}.bindings.{(layer.Spec.Mark == ReportMark.Spans ? "start" : "x")}",
                $"This layer lies along {ReportDerivedOps.Words(layer.Spec.XKind)}; the panel's first layer lies along {ReportDerivedOps.Words(xKind)}. "
                + "Every layer of a chart shares its horizontal axis: put this one in another panel.");

        if (grid is not null && xKind is ReportXKind.Day or ReportXKind.Category)
            Error($"{at}.grid", xKind == ReportXKind.Day
                ? "A chart over days has one slot each day. Remove the grid."
                : "A chart over categories has no grid. Remove the grid.");
        if (grid == 0 && layers.FirstOrDefault(l => l.Paired) is { } paired && PairedAt(paired.Spec.Derived!, $"{paired.At}.derived") is { } pairing)
            Error($"{at}.grid", $"A layer of two sources pairs them slot by slot, so every reading cannot be shown here: "
                + $"{pairing.Label} at {pairing.Path} pairs two sources. Send a grid of 1 minute or more.");
        if (grid is > 0 && ReportGrid.Applies(xKind))
            foreach (var layer in layers)
                if (layer.Folded.FirstOrDefault(f => f.Aggregate is null) is { } loose)
                    Error($"{layer.At}.bindings", $"'{loose.Label}' cannot be folded onto the grid: its values have no way to combine. "
                        + "Remove it from this layer, or send no grid.");

        foreach (var layer in layers)
        {
            if (layer.Spec.Range is not null && xKind is not (ReportXKind.TimeOfDay or ReportXKind.Offset or ReportXKind.Day))
                Error($"{layer.At}.range", xKind is ReportXKind.Time
                    ? "Every layer of a chart over time shares the panel's range. Remove this layer's range, or give the panel this range."
                    : $"A layer has its own range only on a chart over time of day, minutes from an event or days; this one lies along {ReportDerivedOps.Words(xKind)}.");
            var layerRange = layer.Spec.Range ?? range;
            foreach (var read in layer.Reads)
            {
                CheckSpan(read.Dataset, layerRange, layer.Spec.Range is null ? $"{at}.range" : $"{layer.At}.range", Error);
                reads.TryAdd(ReadKey(read.Parameters, layerRange, ReportPanelKind.Chart, read.X), read.Dataset);
            }
            if (layer.Spec.Derived is { } derived)
            {
                var (from, to) = ReportSpecRunner.Resolve(layerRange, now);
                CheckWindows(derived, $"{layer.At}.derived", kind => ReportGrid.Step(kind, grid is > 0 ? grid.Value : ReportGrid.DefaultMinutes, from, to), Error);
            }
        }

        foreach (var (_, trackLayers, _) in tracks)
        {
            var units = new List<string?>();
            foreach (var layer in trackLayers.Where(l => l.Spec.HasAxis))
            {
                if (units.Contains(layer.Spec.Unit))
                    continue;
                if (units.Count == 2)
                {
                    Error(layer.At, $"This track has a left axis in {UnitWords(units[0])} and a right axis in {UnitWords(units[1])}; "
                        + $"this layer is in {UnitWords(layer.Spec.Unit)}. A track has at most two units: put this layer on another track.");
                    continue;
                }
                units.Add(layer.Spec.Unit);
            }
        }

        if (failed)
            return null;

        return new ReportPanelSpec
        {
            Kind = ReportPanelKind.Chart,
            Range = range,
            Grid = grid,
            Tracks = [.. tracks.Select(x => x.Spec)],
        };
    }

    private (ReportTrackSpec Spec, List<ParsedLayer> Layers, string At)? ReadTrack(
        JsonElement track,
        string at,
        DateTimeOffset now,
        bool useMmol,
        HashSet<string> trackIds,
        HashSet<string> layerIds,
        Action<string, string> error)
    {
        if (track.ValueKind != JsonValueKind.Object)
        {
            error(at, "Send each track as an object with 'layers'.");
            return null;
        }
        UnknownKeys(track, at, TrackKeys, error);
        var id = OptionalString(track, "id", $"{at}.id", ReportSpec.MaxIdLength, error);
        if (id is not null && !IdPattern().IsMatch(id))
            error($"{at}.id", "Use only letters, digits, '-' and '_'.");
        if (id is not null && !trackIds.Add(id))
            error($"{at}.id", $"'{id}' is already the id of another track. Track ids must be unique in a panel.");
        var title = OptionalString(track, "title", $"{at}.title", ReportSpec.MaxTitleLength, error);

        double? height = null;
        if (track.TryGetProperty("height", out var h) && h.ValueKind != JsonValueKind.Null)
        {
            if (h.ValueKind == JsonValueKind.Number && h.TryGetDouble(out var weight) && weight is >= ReportTrackSpec.MinHeight and <= ReportTrackSpec.MaxHeight)
                height = weight;
            else
                error($"{at}.height", $"Send a number from {ReportTrackSpec.MinHeight} to {ReportTrackSpec.MaxHeight}: the track's share of the chart's height.");
        }

        if (!track.TryGetProperty("layers", out var layersElement) || layersElement.ValueKind != JsonValueKind.Array || layersElement.GetArrayLength() == 0)
        {
            error($"{at}.layers", "Missing. Send a list of at least one layer.");
            return null;
        }

        var layers = new List<ParsedLayer>();
        var j = 0;
        foreach (var layer in layersElement.EnumerateArray())
        {
            if (ReadLayer(layer, $"{at}.layers[{j}]", now, useMmol, layerIds, error) is { } read)
                layers.Add(read);
            j++;
        }
        if (layers.Count != layersElement.GetArrayLength())
            return null;

        if (id is null)
        {
            id = FreeId(trackIds, "t", trackIds.Count);
            trackIds.Add(id);
        }
        var spec = new ReportTrackSpec
        {
            Id = id,
            Title = title,
            Height = height ?? (layers.All(l => l.Spec.Mark == ReportMark.Spans) ? ReportTrackSpec.LaneHeight : 1),
            Layers = [.. layers.Select(l => l.Spec)],
        };
        return (spec, layers, at);
    }

    private ParsedLayer? ReadLayer(JsonElement layer, string at, DateTimeOffset now, bool useMmol, HashSet<string> layerIds, Action<string, string> error)
    {
        if (layer.ValueKind != JsonValueKind.Object)
        {
            error(at, "Send each layer as an object with 'dataset' or 'derived'.");
            return null;
        }
        var failed = false;
        void Error(string path, string message)
        {
            failed = true;
            error(path, message);
        }

        UnknownKeys(layer, at, LayerKeys, Error);
        var id = OptionalString(layer, "id", $"{at}.id", ReportSpec.MaxIdLength, Error);
        if (id is not null && !IdPattern().IsMatch(id))
            Error($"{at}.id", "Use only letters, digits, '-' and '_'.");
        if (id is not null && !layerIds.Add(id))
            Error($"{at}.id", $"'{id}' is already the id of another layer. Layer ids must be unique in a panel.");
        var title = OptionalString(layer, "title", $"{at}.title", ReportSpec.MaxTitleLength, Error);
        var range = layer.TryGetProperty("range", out var lr) ? ReadRange(lr, $"{at}.range", now, Error) : null;

        var hasDataset = layer.TryGetProperty("dataset", out var d) && d.ValueKind != JsonValueKind.Null;
        var hasDerived = layer.TryGetProperty("derived", out var dv) && dv.ValueKind != JsonValueKind.Null;
        if (hasDataset == hasDerived)
        {
            Error(at, hasDataset ? "Send 'dataset' or 'derived', not both." : "Send 'dataset' (the data to read) or 'derived' (a series the API works out).");
            return null;
        }

        IReadOnlyList<ReportField> fields;
        IReadOnlyList<ReportMark> marks;
        Func<ReportMark, ReportLayerBindings?> defaults;
        IReadOnlyList<LayerRead> reads;
        ReportDatasetParameters? parameters = null;
        ParsedDerived? derived = null;
        IReportDataset? dataset = null;
        string label;
        if (hasDataset)
        {
            dataset = ReadDataset(layer, at, Error, out parameters);
            if (dataset is null || parameters is null)
                return null;
            if (dataset.Marks.Count == 0)
            {
                Error($"{at}.dataset.name", $"{dataset.Label} cannot be drawn in a chart. Show it in a panel of its own as "
                    + $"{string.Join(" or ", dataset.PanelKinds.Select(PanelKindWords))}.");
                return null;
            }
            fields = [.. dataset.FieldsFor(ReportPanelKind.Chart).Where(f => !f.Companion).Select(f => f.ResolveUnit(useMmol))];
            marks = dataset.Marks;
            defaults = dataset.DefaultBindings;
            label = dataset.Label;
            reads = [];
        }
        else
        {
            derived = ReadDerived(dv, $"{at}.derived", 0, useMmol, Error);
            if (derived is null)
                return null;
            fields = derived.Fields;
            marks = derived.Op.Marks(derived.Spec);
            defaults = mark => derived.Op.DefaultBindings(mark, derived.Spec);
            label = derived.Op.Label;
            reads = derived.Reads;
        }

        ReportMark? mark = marks[0];
        if (layer.TryGetProperty("mark", out var m) && m.ValueKind != JsonValueKind.Null)
        {
            mark = m.ValueKind == JsonValueKind.String ? ReportWire.Parse<ReportMark>(m.GetString()) : null;
            if (mark is null)
                Error($"{at}.mark", $"Send one of: {ReportWire.List(marks)}.");
            else if (!marks.Contains(mark.Value))
            {
                Error($"{at}.mark", $"{label} cannot be drawn as {ReportMarks.Label(mark.Value).ToLowerInvariant()}. Send one of: {ReportWire.List(marks)}.");
                mark = null;
            }
        }
        if (mark is not { } drawn)
            return null;

        layer.TryGetProperty("bindings", out var b);
        var companions = dataset?.FieldsFor(ReportPanelKind.Chart).Where(f => f.Companion).Select(f => f.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var bindings = ReadLayerBindings(b, $"{at}.bindings", drawn, fields, companions, defaults(drawn), Error);
        if (failed || bindings is null)
            return null;

        if (dataset is not null)
            reads = [new LayerRead(dataset, parameters!, bindings.Value.Bindings.X)];

        if (id is null)
        {
            id = FreeId(layerIds, "l", layerIds.Count);
            layerIds.Add(id);
        }
        return new ParsedLayer(
            new ReportLayerSpec
            {
                Id = id,
                Title = title,
                Dataset = parameters,
                Derived = derived?.Spec,
                Mark = drawn,
                Bindings = bindings.Value.Bindings,
                Range = range,
                XKind = bindings.Value.XKind,
                Unit = bindings.Value.Unit,
                HasAxis = ReportMarks.HasAxis(drawn),
            },
            at,
            reads,
            dataset is null ? [] : bindings.Value.Folded,
            derived is not null && IsPaired(derived.Spec));
    }

    /// <summary>Why <paramref name="field"/> cannot be a source, in words.</summary>
    private static string NotDataWords(ReportField field) => field.Setting switch
    {
        ReportSetting.Profile => $"'{field.Label}' is a value of the profile's settings, which a derived series is never compared with.",
        ReportSetting.ProfileRelative => $"'{field.Label}' is worked out against the profile's scheduled basal, so a derived series of it could show the schedule.",
        ReportSetting.FixedLimit => $"'{field.Label}' is a fixed limit that is the same for everyone, not data.",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field.Setting, null),
    };

    /// <summary>The first op in <paramref name="derived"/>, outermost first, that pairs two sources, in words, and its path.</summary>
    private static (string Label, string Path)? PairedAt(ReportDerivedSpec derived, string path)
    {
        if (derived.Second is not null)
            return (ReportDerivedOps.Of(derived).Label, path);
        foreach (var (source, key) in derived.Sources.Zip(new[] { "a", "b" }))
            if (source.Derived is { } inner && PairedAt(inner, $"{path}.{key}.derived") is { } found)
                return found;
        return null;
    }

    /// <summary>
    /// Refuses a window that is not a whole number of the grid's slots, at its path, so the window a layer states is
    /// the one it averages over. <paramref name="stepOf"/> gives the step a source along an axis is folded at.
    /// </summary>
    /// <returns>The axis <paramref name="derived"/> lies along.</returns>
    private ReportXKind? CheckWindows(ReportDerivedSpec derived, string path, Func<ReportXKind, int> stepOf, Action<string, string> error)
    {
        var op = ReportDerivedOps.Of(derived);
        ReportXKind? sourceKind = null;
        foreach (var (source, key) in derived.Sources.Zip(new[] { "a", "b" }))
        {
            var kind = source.Derived is { } inner
                ? CheckWindows(inner, $"{path}.{key}.derived", stepOf, error)
                : registry.Find(source.Dataset!.DatasetName)?.FieldsFor(ReportPanelKind.Chart)
                    .FirstOrDefault(f => !f.Companion && f.AxisKind is { } k && ReportDerivedOps.GridKinds.Contains(k))?.AxisKind;
            sourceKind ??= kind;
        }
        if (sourceKind is not { } along)
            return null;
        var minutes = derived switch
        {
            RollingMeanSpec rolling => rolling.Minutes,
            BandSdSpec band => band.Minutes,
            _ => (int?)null,
        };
        if (minutes is { } window && along != ReportXKind.Day && stepOf(along) is var step && window % step != 0)
            error($"{path}.minutes", $"The grid here is {step} minutes, so the window is a whole number of its slots. "
                + $"Send a multiple of {step}, such as {Math.Max(step, (int)Math.Round(window / (double)step) * step)}.");
        return op.OutputXKind(along);
    }

    /// <summary>Whether a derived series pairs two sources anywhere in it, a source's own derivation included.</summary>
    internal static bool IsPaired(ReportDerivedSpec derived) =>
        derived.Second is not null || derived.Sources.Any(s => s.Derived is { } inner && IsPaired(inner));

    private ParsedDerived? ReadDerived(JsonElement element, string at, int depth, bool useMmol, Action<string, string> error)
    {
        var names = string.Join(", ", ReportDerivedOps.All.Select(o => o.Name));
        if (element.ValueKind != JsonValueKind.Object)
        {
            error(at, $"Send an object with 'op', one of: {names}.");
            return null;
        }
        if (!element.TryGetProperty("op", out var opElement) || opElement.ValueKind != JsonValueKind.String)
        {
            error($"{at}.op", $"Missing. Send one of: {names}.");
            return null;
        }
        if (ReportDerivedOps.Find(opElement.GetString()!) is not { } op)
        {
            error($"{at}.op", $"'{Shorten(opElement.GetString()!)}' is not a derived series. Send one of: {names}.");
            return null;
        }

        var failed = false;
        void Error(string path, string message)
        {
            failed = true;
            error(path, message);
        }

        UnknownKeys(element, at, [.. ParameterProperties(op.SpecType).Keys.Prepend("op")], Error);
        if (failed)
            return null;

        ReportDerivedSpec? spec;
        try
        {
            spec = (ReportDerivedSpec?)JsonSerializer.Deserialize(WithoutName(element, "op", "a", "b"), op.SpecType, ParameterOptions);
        }
        catch (JsonException ex)
        {
            var key = ex.Path is { Length: > 2 } path && path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : null;
            var type = key is null ? null : PathType(op.SpecType, key);
            Error(key is null ? at : $"{at}.{key}", type is null ? "Send a value of the kind this setting takes." : $"Send {Expected(type)}.");
            return null;
        }
        if (spec is null)
            return null;

        var reads = new List<LayerRead>();
        var a = ReadSource(element, "a", at, depth, useMmol, reads, Error);
        var b = op.SourceCount == 2 ? ReadSource(element, "b", at, depth, useMmol, reads, Error) : null;
        if (failed || a is null || (op.SourceCount == 2 && b is null))
            return null;

        if (!op.SourceXKinds.Contains(a.Value.Info.XKind))
            Error(at, $"A {op.Label.ToLowerInvariant()} needs sources along {string.Join(" or ", op.SourceXKinds.Select(ReportDerivedOps.Words))}; "
                + $"this one lies along {ReportDerivedOps.Words(a.Value.Info.XKind)}.");
        foreach (var (field, message) in op.Validate(spec, a.Value.Info, b?.Info))
            Error($"{at}.{field}", message);
        if (failed)
            return null;

        spec = spec with { A = a.Value.Spec };
        if (spec is ReportPairedSpec paired)
            spec = paired with { B = b!.Value.Spec };
        return new ParsedDerived(spec, op, op.OutputXKind(a.Value.Info.XKind), op.OutputFields(spec, a.Value.Info, b?.Info), reads);
    }

    private (ReportSourceSpec Spec, ReportSourceInfo Info)? ReadSource(
        JsonElement owner, string key, string at, int depth, bool useMmol, List<LayerRead> reads, Action<string, string> error)
    {
        var path = $"{at}.{key}";
        if (!owner.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            error(path, "Missing. Send a source: 'dataset' and 'field', or 'derived' and 'field'.");
            return null;
        }
        UnknownKeys(element, path, SourceKeys, error);
        var hasDataset = element.TryGetProperty("dataset", out var d) && d.ValueKind != JsonValueKind.Null;
        var hasDerived = element.TryGetProperty("derived", out var dv) && dv.ValueKind != JsonValueKind.Null;
        if (hasDataset == hasDerived)
        {
            error(path, hasDataset ? "Send 'dataset' or 'derived', not both." : "Send 'dataset' (the data to read) or 'derived'.");
            return null;
        }
        string? fieldName = element.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;

        if (hasDataset)
        {
            var dataset = ReadDataset(element, path, error, out var parameters);
            if (dataset is null || parameters is null)
                return null;
            var fields = dataset.FieldsFor(ReportPanelKind.Chart).Where(x => !x.Companion).ToList();
            var axis = fields.FirstOrDefault(x => x.AxisKind is { } kind && ReportDerivedOps.GridKinds.Contains(kind));
            if (axis is null)
            {
                error($"{path}.dataset.name", $"{dataset.Label} has no axis of time, minutes, time of day or days, so a series cannot be derived from it.");
                return null;
            }
            var usable = fields.Where(x => x.IsNumber && x.Aggregate is not null && x.Setting == ReportSetting.None).ToList();
            var field = usable.FirstOrDefault(x => x.Name == fieldName);
            if (field is null)
            {
                var setting = fields.FirstOrDefault(x => x.Name == fieldName && x.Setting != ReportSetting.None);
                error($"{path}.field", setting is not null
                    ? $"{NotDataWords(setting)} A derived series uses the data only. Send a field of the data: {string.Join(", ", usable.Select(x => x.Name))}."
                    : (fieldName is null ? "Missing. " : $"'{Shorten(fieldName)}' cannot be a source. ")
                      + $"Send a number field of {dataset.Label}: {string.Join(", ", usable.Select(x => x.Name))}.");
                return null;
            }
            int? series = null;
            var seriesCount = dataset.SeriesCount(parameters);
            var seriesSent = element.TryGetProperty("series", out var s) && s.ValueKind != JsonValueKind.Null;
            if (seriesSent)
            {
                if (dataset.SeriesField is not null && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n) && n >= 1 && n <= seriesCount)
                    series = n;
                else
                {
                    error($"{path}.series", dataset.SeriesField is null
                        ? "This data has no groups. Remove the group number."
                        : $"Send the number of the group this source reads, 1 to {seriesCount}.");
                    return null;
                }
            }
            else if (seriesCount > 1)
            {
                error($"{path}.series", $"This data comes in {seriesCount} groups, and a source reads one. "
                    + $"Send the number of the group this source reads, 1 to {seriesCount}.");
                return null;
            }
            reads.Add(new LayerRead(dataset, parameters, axis.Name));
            var resolved = field.ResolveUnit(useMmol);
            return (new ReportSourceSpec { Dataset = parameters, Field = field.Name, Series = series },
                new ReportSourceInfo(resolved, axis.AxisKind!.Value, resolved.Label));
        }

        if (depth >= 1)
        {
            error($"{path}.derived", "A source can be derived from datasets only, one level deep. Use a dataset here.");
            return null;
        }
        var derived = ReadDerived(dv, $"{path}.derived", depth + 1, useMmol, error);
        if (derived is null)
            return null;
        if (!ReportDerivedOps.GridKinds.Contains(derived.XKind))
        {
            error($"{path}.derived.op", $"A {derived.Op.Label.ToLowerInvariant()} lies along {ReportDerivedOps.Words(derived.XKind)}, so it cannot be a source. "
                + "A source lies along time, minutes from an event, time of day or days.");
            return null;
        }
        var output = derived.Fields.Where(x => x.IsNumber).ToList();
        var chosen = output.FirstOrDefault(x => x.Name == fieldName);
        if (chosen is null)
        {
            error($"{path}.field", (fieldName is null ? "Missing. " : $"'{Shorten(fieldName)}' is not a field of this series. ")
                + $"Send one of: {string.Join(", ", output.Select(x => x.Name))}.");
            return null;
        }
        reads.AddRange(derived.Reads);
        return (new ReportSourceSpec { Derived = derived.Spec, Field = chosen.Name }, new ReportSourceInfo(chosen, derived.XKind, chosen.Label));
    }

    /// <summary>A layer's bindings for <paramref name="mark"/>, filled from <paramref name="defaults"/> and then from the mark's own; null when they fail.</summary>
    private static (ReportLayerBindings Bindings, ReportXKind XKind, string? Unit, IReadOnlyList<ReportField> Folded)? ReadLayerBindings(
        JsonElement element,
        string at,
        ReportMark mark,
        IReadOnlyList<ReportField> fields,
        IReadOnlySet<string> companions,
        ReportLayerBindings? defaults,
        Action<string, string> error)
    {
        var failed = false;
        void Error(string path, string message)
        {
            failed = true;
            error(path, message);
        }

        var present = element.ValueKind == JsonValueKind.Object;
        if (element.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
        {
            Error(at, "Send the bindings as an object.");
            return null;
        }
        var slots = ReportMarks.Slots(mark);
        var markWords = ReportMarks.Label(mark).ToLowerInvariant();
        if (present)
        {
            UnknownKeys(element, at, LayerBindingKeys, Error);
            foreach (var property in element.EnumerateObject())
                if (LayerBindingKeys.Contains(property.Name) && slots.All(s => s.Key != property.Name) && property.Value.ValueKind != JsonValueKind.Null)
                    Error($"{at}.{property.Name}", $"A layer of {markWords} does not use '{property.Name}'. Remove it.");
        }
        if (failed)
            return null;

        bool Sent(string key) => present && element.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null;

        string? Text(string key, string? fallback)
        {
            if (!Sent(key))
                return fallback;
            var v = element.GetProperty(key);
            if (v.ValueKind == JsonValueKind.String)
                return v.GetString();
            Error($"{at}.{key}", "Send one field name as text.");
            return null;
        }

        IReadOnlyList<string>? List(string key, IReadOnlyList<string>? fallback)
        {
            if (!Sent(key))
                return fallback;
            var v = element.GetProperty(key);
            if (v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String))
                return [.. v.EnumerateArray().Select(x => x.GetString()!)];
            Error($"{at}.{key}", "Send a list of field names, for example [\"glucose\"].");
            return null;
        }

        var names = string.Join(", ", fields.Select(f => f.Name));
        ReportField? Field(string key, string name)
        {
            var field = fields.FirstOrDefault(f => f.Name == name);
            if (field is null && companions.Contains(name))
                Error($"{at}.{key}", $"'{name}' comes with every row and cannot be drawn on its own. Fields: {names}.");
            else if (field is null)
                Error($"{at}.{key}", $"'{Shorten(name)}' is not a field here. Fields: {names}.");
            return field;
        }

        ReportField? First(ReportMarkSlot slot, IEnumerable<ReportField>? skip = null) =>
            fields.Where(f => ReportMarks.Accepts(slot, f)).Except(skip ?? []).FirstOrDefault();

        ReportMarkSlot SlotOf(string key) => slots.Single(s => s.Key == key);

        if (mark == ReportMark.Spans)
        {
            var startName = Text("start", defaults?.Start ?? First(SlotOf("start"))?.Name);
            var endName = Text("end", defaults?.End ?? fields.Where(f => ReportMarks.Accepts(SlotOf("end"), f)).Skip(1).FirstOrDefault()?.Name);
            var categoryName = Text("category", defaults?.Category ?? First(SlotOf("category"))?.Name);
            foreach (var (key, name) in new[] { ("start", startName), ("end", endName), ("category", categoryName) })
            {
                if (name is null)
                    Error($"{at}.{key}", $"Missing. Send a field of kind {SlotOf(key).Accepts[0]}.");
                else if (Field(key, name) is { } field && !ReportMarks.Accepts(SlotOf(key), field))
                    Error($"{at}.{key}", $"'{name}' is a {ReportWire.Name(field.Kind)} field. Send a field of kind {SlotOf(key).Accepts[0]}.");
            }
            if (failed)
                return null;
            return (new ReportLayerBindings { Start = startName, End = endName, Category = categoryName }, ReportXKind.Time, null, []);
        }

        var xName = Text("x", defaults?.X ?? First(SlotOf("x"))?.Name);
        ReportXKind? xKind = null;
        if (xName is null)
            Error($"{at}.x", $"Missing. Send a field of kind: {string.Join(", ", SlotOf("x").Accepts)}.");
        else if (Field("x", xName) is { } xField)
        {
            if (xField.AxisKind is { } kind && ReportMarks.XKinds(mark).Contains(kind))
                xKind = kind;
            else
                Error($"{at}.x", $"A layer of {markWords} cannot lie along '{xField.Label}'. Send a field of kind: {string.Join(", ", SlotOf("x").Accepts)}.");
        }

        var ySlot = SlotOf("y");
        IReadOnlyList<string>? fallbackY = defaults?.Y
            ?? (mark == ReportMark.Markers ? [.. fields.Where(f => f.Marker is not null).Select(f => f.Name)]
                : mark == ReportMark.Band ? null
                : First(ySlot) is { } firstNumber ? [firstNumber.Name] : null);
        var y = List("y", fallbackY) ?? [];
        var edges = mark == ReportMark.Band ? List("edges", defaults?.Edges) : null;
        var series = slots.Any(s => s.Key == "series") ? Text("series", defaults?.Series) : null;
        var fit = mark == ReportMark.Points ? Text("fit", defaults?.Fit) : null;

        if (ySlot.Required && y.Count == 0)
            Error($"{at}.y", mark == ReportMark.Markers
                ? "Missing. Send the marker fields to draw."
                : $"Missing. Send the fields to draw. Fields: {names}.");
        if (mark == ReportMark.Band && y.Count > 1)
            Error($"{at}.y", "Send one field: the middle line, or none.");
        if (y.Distinct().Count() != y.Count)
            Error($"{at}.y", "A field is named twice.");

        var drawn = new List<ReportField>();
        foreach (var name in y)
        {
            if (Field("y", name) is not { } field)
                continue;
            if (!ReportMarks.Accepts(ySlot, field))
                Error($"{at}.y", mark == ReportMark.Markers
                    ? $"'{field.Label}' has no marker. Send one of: {string.Join(", ", fields.Where(f => f.Marker is not null).Select(f => f.Name))}."
                    : $"'{field.Label}' is a {ReportWire.Name(field.Kind)} field. A layer of {markWords} needs number fields.");
            else
                drawn.Add(field);
        }
        if (mark != ReportMark.Markers && drawn.Select(f => f.Unit).Distinct().Count() > 1)
            Error($"{at}.y", $"The fields {string.Join(", ", drawn.Select(f => $"{f.Label} ({UnitWords(f.Unit)})"))} have different units. "
                + "Put a field of another unit in a layer of its own.");

        var edgeFields = new List<ReportField>();
        if (mark == ReportMark.Band)
        {
            if (edges is not { Count: 2 or 4 })
                Error($"{at}.edges", "Send two fields (the band's low and high edges) or four (outer low, inner low, inner high, outer high).");
            else
            {
                if (edges.Distinct().Count() != edges.Count)
                    Error($"{at}.edges", "A field is named twice.");
                foreach (var name in edges)
                    if (Field("edges", name) is { } field)
                    {
                        if (!field.IsNumber)
                            Error($"{at}.edges", $"'{field.Label}' is a {ReportWire.Name(field.Kind)} field. An edge needs a number field.");
                        else
                            edgeFields.Add(field);
                    }
                var unit = drawn.FirstOrDefault()?.Unit ?? edgeFields.FirstOrDefault()?.Unit;
                if (edgeFields.Count == edges.Count && edgeFields.Concat(drawn).Any(f => f.Unit != unit))
                    Error($"{at}.edges", $"Every edge must be in the unit of the middle line ({UnitWords(unit)}).");
            }
        }

        ReportField? fitField = null;
        if (fit is not null && Field("fit", fit) is { } fitted)
        {
            if (!fitted.IsNumber || (drawn.Count > 0 && fitted.Unit != drawn[0].Unit))
                Error($"{at}.fit", $"The line through the points must be a number field in the unit of the points ({UnitWords(drawn.FirstOrDefault()?.Unit)}).");
            else
                fitField = fitted;
        }

        if (series is not null && Field("series", series) is { } seriesField && seriesField.Kind != ReportFieldKind.Category)
            Error($"{at}.series", $"'{seriesField.Label}' is a {ReportWire.Name(seriesField.Kind)} field. Send a category field.");

        bool? stacked = null;
        ReportBarOrientation? orientation = null;
        if (mark == ReportMark.Bars)
        {
            stacked = defaults?.Stacked ?? false;
            if (Sent("stacked"))
            {
                var s = element.GetProperty("stacked");
                if (s.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    stacked = s.GetBoolean();
                else
                    Error($"{at}.stacked", "Send true or false.");
            }
            orientation = defaults?.Orientation ?? ReportBarOrientation.Vertical;
            if (Sent("orientation"))
            {
                orientation = element.GetProperty("orientation") is { ValueKind: JsonValueKind.String } o
                    ? ReportWire.Parse<ReportBarOrientation>(o.GetString())
                    : null;
                if (orientation is null)
                    Error($"{at}.orientation", "Send \"vertical\" or \"horizontal\".");
            }
        }

        if (failed || xKind is null)
            return null;
        var axisUnit = mark switch
        {
            ReportMark.Markers => null,
            ReportMark.Band => edgeFields.FirstOrDefault()?.Unit,
            _ => drawn.FirstOrDefault()?.Unit,
        };
        return (
            new ReportLayerBindings
            {
                X = xName,
                Y = y.Count == 0 ? null : y,
                Edges = edges,
                Series = series,
                Fit = fit,
                Stacked = stacked,
                Orientation = orientation,
            },
            xKind.Value,
            axisUnit,
            [.. drawn, .. edgeFields, .. new[] { fitField }.OfType<ReportField>()]);
    }

    /// <summary>An error at <paramref name="at"/> when <paramref name="range"/> is longer than <paramref name="dataset"/> reads.</summary>
    private static void CheckSpan(IReportDataset dataset, ReportRange range, string at, Action<string, string> error)
    {
        var span = range.Hours is { } hours ? TimeSpan.FromHours(hours) : range.To!.Value - range.From!.Value;
        if (span > dataset.MaxRange)
            error(at, $"{dataset.Label} covers at most {Describe(dataset.MaxRange)}; this range is {Describe(span)}. "
                + "Shorten it, or split it into more than one panel.");
    }

    internal static string PanelKindWords(ReportPanelKind kind) => kind switch
    {
        ReportPanelKind.Chart => "a chart",
        ReportPanelKind.StatTiles => "figures",
        ReportPanelKind.Table => "a table",
        ReportPanelKind.Comparison => "two periods side by side",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    internal static string UnitWords(string? unit) => unit ?? "no unit";

    private static string LayerWords(ParsedLayer layer) =>
        layer.Spec.Title ?? (layer.Spec.Derived is { } d ? ReportDerivedOps.Of(d).Label.ToLowerInvariant() : layer.Spec.Dataset?.DatasetName ?? "a layer");

    private static string FreeId(HashSet<string> taken, string prefix, int index)
    {
        for (var n = index + 1; ; n++)
        {
            var id = $"{prefix}{n}";
            if (!taken.Contains(id))
                return id;
        }
    }
}
