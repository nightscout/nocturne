using System.Buffers;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Nocturne.API.Mcp.Reports;

/// <summary>One thing wrong with a spec: where (<see cref="Path"/>, e.g. <c>panels[1].bindings.y</c>) and what to change.</summary>
internal sealed record ReportSpecError(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <param name="Shortened">The panels whose range was cut to their dataset's limit, by id; only when the caller asked for that.</param>
internal sealed record ReportSpecValidation(ReportSpec? Spec, IReadOnlyList<ReportSpecError> Errors, IReadOnlySet<string>? Shortened = null)
{
    public bool IsValid => Errors.Count == 0 && Spec is not null;
}

/// <summary>
/// Reads <c>render_report</c>'s <c>spec</c> argument into a normalised <see cref="ReportSpec"/>,
/// or lists every error in it. Normalised: version stamped, panel ids assigned, each panel's range
/// and bindings filled from the report's or the dataset's defaults.
/// </summary>
/// <remarks>
/// It reads the JSON itself rather than deserialising the polymorphic type: the serializer stops
/// at the first unknown dataset name or enum value, with a message that names neither the panel
/// nor the choices and does name .NET types, and the model needs the panel and the choices to
/// repair the spec. Every value is checked for its JSON kind before it is read, so a malformed spec
/// is an error list, never an exception.
/// </remarks>
internal sealed partial class ReportSpecValidator(IReportDatasetRegistry registry, TimeProvider time)
{
    /// <summary>The earliest date a range may start: before it there is no data, and older dates overflow the readers' conversions.</summary>
    internal static readonly DateTimeOffset EarliestDate = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions ParameterOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.Strict,
    };

    private static readonly string[] SpecKeys = ["version", "title", "range", "panels"];
    private static readonly string[] PanelKeys = ["id", "title", "dataset", "primitive", "bindings", "range"];
    private static readonly string[] RangeKeys = ["hours", "from", "to"];
    private static readonly string[] BindingKeys = ["x", "y", "band", "columns", "series", "stacked", "orientation"];

    /// <param name="clampRanges">
    /// Cut a range longer than its dataset covers to the latest part it covers, in place of refusing it:
    /// for refinements the view asks for (load earlier data), never for a spec a model sent.
    /// </param>
    public ReportSpecValidation Validate(JsonElement spec, bool useMmol = false, bool clampRanges = false)
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
        var shortenedAt = new HashSet<int>();
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
                if (ReadPanel(panel, index, reportRange, ids, now, useMmol, clampRanges ? shortenedAt : null, Error) is { } read)
                    panels.Add(read);
                index++;
            }

            foreach (var group in panels.GroupBy(p => p.Dataset.DatasetName))
            {
                if (registry.Find(group.Key) is { } heavy && group.Count() > heavy.MaxPanelsPerReport)
                    Error($"panels[{panels.IndexOf(group.ElementAt(heavy.MaxPanelsPerReport))}].dataset.name",
                        $"A report holds at most {heavy.MaxPanelsPerReport} {heavy.Name} panels; this one has {group.Count()}. "
                        + "Split them into more than one report.");
            }

            for (var i = 0; i < panels.Count; i++)
            {
                if (panels[i].Id is null)
                {
                    var assigned = NextFreeId(ids, i);
                    ids.Add(assigned);
                    panels[i] = panels[i] with { Id = assigned };
                }
            }
        }

        if (errors.Count > 0)
            return new ReportSpecValidation(null, errors);

        return new ReportSpecValidation(
            new ReportSpec { Version = ReportSpec.CurrentVersion, Title = title, Range = reportRange, Panels = panels },
            errors,
            clampRanges ? shortenedAt.Select(i => panels[i].Id!).ToHashSet(StringComparer.Ordinal) : null);
    }

    private ReportPanelSpec? ReadPanel(
        JsonElement panel,
        int index,
        ReportRange? reportRange,
        HashSet<string> ids,
        DateTimeOffset now,
        bool useMmol,
        HashSet<int>? shortened,
        Action<string, string> error)
    {
        var at = $"panels[{index}]";
        if (panel.ValueKind != JsonValueKind.Object)
        {
            error(at, "Send each panel as an object with 'dataset' and 'primitive'.");
            return null;
        }

        var failed = false;
        void Error(string path, string message)
        {
            failed = true;
            error(path, message);
        }

        UnknownKeys(panel, at, PanelKeys, Error);

        var id = OptionalString(panel, "id", $"{at}.id", ReportSpec.MaxIdLength, Error);
        if (id is not null && !IdPattern().IsMatch(id))
            Error($"{at}.id", "Use only letters, digits, '-' and '_'.");
        if (id is not null && !ids.Add(id))
            Error($"{at}.id", $"'{id}' is already the id of another panel. Panel ids must be unique.");

        var title = OptionalString(panel, "title", $"{at}.title", ReportSpec.MaxTitleLength, Error);

        var dataset = ReadDataset(panel, at, Error, out var parameters);

        ReportPrimitive? primitive = null;
        if (!panel.TryGetProperty("primitive", out var p) || p.ValueKind != JsonValueKind.String)
        {
            Error($"{at}.primitive", $"Missing. Send one of: {PrimitiveList(ReportPrimitives.All)}.");
        }
        else if (ReportPrimitives.Parse(p.GetString()) is not { } parsed)
        {
            Error($"{at}.primitive", $"'{Shorten(p.GetString()!)}' is not a primitive. Send one of: {PrimitiveList(ReportPrimitives.All)}.");
        }
        else if (dataset is not null && !dataset.AllowedPrimitives.Contains(parsed))
        {
            Error($"{at}.primitive",
                $"{dataset.Name} cannot be drawn as {parsed.WireName()}. Send one of: {PrimitiveList(dataset.AllowedPrimitives)}.");
        }
        else
        {
            primitive = parsed;
        }

        var range = panel.TryGetProperty("range", out var pr) ? ReadRange(pr, $"{at}.range", now, Error) : null;
        range ??= reportRange;
        if (dataset is not null && primitive is { } drawn)
        {
            range ??= new ReportRange { Hours = (int)dataset.DefaultRange.TotalHours };
            var max = dataset.MaxRange(drawn);
            var span = range.Hours is { } hours ? TimeSpan.FromHours(hours) : range.To!.Value - range.From!.Value;
            if (span > max && shortened is not null)
            {
                range = range.Hours is not null
                    ? new ReportRange { Hours = (int)max.TotalHours }
                    : new ReportRange { From = range.To!.Value - max, To = range.To };
                span = max;
                shortened.Add(index);
            }
            if (span > max)
                Error($"{at}.range",
                    $"{dataset.Name} drawn as {drawn.WireName()} covers at most {Describe(max)}; this range is {Describe(span)}. "
                    + "Shorten it, or split it into more than one panel.");
            if (parameters is not null)
                foreach (var (field, message) in dataset.ValidatePanel(parameters, drawn, span))
                    Error($"{at}.dataset.{field}", message);
        }

        ReportBindings? bindings = null;
        if (dataset is not null && primitive is { } kind)
        {
            panel.TryGetProperty("bindings", out var b);
            bindings = ReadBindings(b, $"{at}.bindings", dataset, kind, useMmol, Error);
        }

        if (failed || dataset is null || parameters is null || primitive is null)
            return null;

        return new ReportPanelSpec
        {
            Id = id,
            Title = title,
            Dataset = parameters,
            Primitive = primitive.Value,
            Bindings = bindings,
            Range = range,
        };
    }

    private IReportDataset? ReadDataset(
        JsonElement panel,
        string at,
        Action<string, string> error,
        out ReportDatasetParameters? parameters)
    {
        parameters = null;
        var names = string.Join(", ", registry.All.Select(d => d.Name));

        if (!panel.TryGetProperty("dataset", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            error($"{at}.dataset", $"Missing. Send an object with 'name', one of: {names}.");
            return null;
        }

        if (!element.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            error($"{at}.dataset.name", $"Missing. Send one of: {names}.");
            return null;
        }

        var name = nameElement.GetString()!;
        if (registry.Find(name) is not { } dataset)
        {
            error($"{at}.dataset.name", $"'{Shorten(name)}' is not a dataset. Send one of: {names}.");
            return null;
        }

        var properties = ParameterProperties(dataset.ParametersType);
        var known = properties.Keys.Prepend("name").ToArray();
        var failed = false;
        UnknownKeys(element, $"{at}.dataset", known, (path, message) =>
        {
            failed = true;
            error(path, message + " Call describe_dataset for its parameters.");
        });
        if (failed)
            return dataset;

        try
        {
            parameters = (ReportDatasetParameters?)JsonSerializer.Deserialize(WithoutName(element), dataset.ParametersType, ParameterOptions);
        }
        catch (JsonException ex)
        {
            var key = ex.Path is { Length: > 2 } path && path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : null;
            var type = key is null ? null : PathType(dataset.ParametersType, key);
            error(key is null ? $"{at}.dataset" : $"{at}.dataset.{key}", (key, type) switch
            {
                (null, _) => "Send a value of the kind describe_dataset lists.",
                (_, null) => "Not a known key. Call describe_dataset for its parameters.",
                _ => $"Send {Expected(type)}.",
            });
            return dataset;
        }

        if (parameters is null)
            return dataset;

        foreach (var (field, message) in dataset.Validate(parameters))
            error($"{at}.dataset.{field}", message);

        return dataset;
    }

    /// <summary>The parameters a dataset's type binds, by wire name.</summary>
    private static Dictionary<string, Type> ParameterProperties(Type parametersType) =>
        parametersType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), p => p.PropertyType, StringComparer.Ordinal);

    /// <summary>
    /// The type a JSON path into <paramref name="root"/> lands on, <c>cohorts[0].anchor</c> say, or
    /// null where the path names no property.
    /// </summary>
    internal static Type? PathType(Type root, string path)
    {
        var type = root;
        foreach (var segment in path.Split('.'))
        {
            var name = segment;
            var indexed = 0;
            while (name.EndsWith(']') && name.LastIndexOf('[') is var open and > 0)
            {
                name = name[..open];
                indexed++;
            }
            var property = ParameterProperties(type).GetValueOrDefault(name);
            if (property is null)
                return null;
            type = property;
            for (var i = 0; i < indexed; i++)
            {
                if (ElementType(type) is not { } element)
                    return null;
                type = element;
            }
        }
        return type;
    }

    private static Type? ElementType(Type type) =>
        type.IsArray
            ? type.GetElementType()
            : type.IsGenericType && type.GetGenericArguments() is [var element] && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)
                ? element
                : null;

    private static string Expected(Type type) =>
        (Nullable.GetUnderlyingType(type) ?? type) switch
        {
            var t when t == typeof(int) || t == typeof(long) => "a whole number",
            var t when t == typeof(double) || t == typeof(decimal) => "a number",
            var t when t == typeof(bool) => "true or false",
            var t when t == typeof(string) => "text",
            var t when t == typeof(Guid) => "an id, as find_foods returns it",
            var t when t == typeof(DateOnly) => "a date, yyyy-MM-dd",
            var t when t.IsEnum => "one of: " + string.Join(", ", EnumNames(t)),
            var t when t != typeof(string) && ElementType(t) is { } element => $"a list, each item {Expected(element)}",
            var t when t.IsClass => "an object with the keys describe_dataset lists",
            _ => "a value of the kind describe_dataset lists",
        };

    /// <summary>An enum's values as they are sent: their <see cref="JsonStringEnumMemberNameAttribute"/>, else their names.</summary>
    private static IEnumerable<string> EnumNames(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? f.Name);

    /// <summary>The dataset object without its discriminator, written property by property so a repeated key cannot throw.</summary>
    private static byte[] WithoutName(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == "name")
                    continue;
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static ReportBindings ReadBindings(
        JsonElement element,
        string at,
        IReportDataset dataset,
        ReportPrimitive primitive,
        bool useMmol,
        Action<string, string> error)
    {
        var present = element.ValueKind == JsonValueKind.Object;
        if (element.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
        {
            error(at, "Send the bindings as an object.");
            present = false;
        }

        if (present)
            UnknownKeys(element, at, BindingKeys, error);

        string? Text(string key, out bool sent)
        {
            sent = present && element.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null;
            if (!sent)
                return null;
            var v = element.GetProperty(key);
            if (v.ValueKind == JsonValueKind.String)
                return v.GetString();
            error($"{at}.{key}", "Send one field name as text.");
            return null;
        }

        var malformed = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<string>? List(string key)
        {
            if (!present || !element.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
                return null;
            if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String))
                return [.. value.EnumerateArray().Select(v => v.GetString()!)];
            error($"{at}.{key}", "Send a list of field names, for example [\"glucose\"].");
            malformed.Add(key);
            return null;
        }

        bool? stacked = null;
        var stackedSent = present && element.TryGetProperty("stacked", out var stackedValue) && stackedValue.ValueKind != JsonValueKind.Null;
        if (stackedSent)
        {
            if (element.GetProperty("stacked").ValueKind is JsonValueKind.True or JsonValueKind.False)
                stacked = element.GetProperty("stacked").GetBoolean();
            else
                error($"{at}.stacked", "Send true or false.");
        }
        if (primitive != ReportPrimitive.Bar && stackedSent)
            error($"{at}.stacked", $"{primitive.WireName()} does not use 'stacked'. Remove it.");

        ReportBarOrientation? orientation = null;
        var orientationSent = present && element.TryGetProperty("orientation", out var orientationValue) && orientationValue.ValueKind != JsonValueKind.Null;
        if (orientationSent)
        {
            orientation = element.GetProperty("orientation") is { ValueKind: JsonValueKind.String } o
                ? o.GetString() switch { "vertical" => ReportBarOrientation.Vertical, "horizontal" => ReportBarOrientation.Horizontal, _ => null }
                : null;
            if (orientation is null)
                error($"{at}.orientation", "Send \"vertical\" or \"horizontal\".");
        }
        if (primitive != ReportPrimitive.Bar && orientationSent)
            error($"{at}.orientation", $"{primitive.WireName()} does not use 'orientation'. Remove it.");

        var x = Text("x", out var xSent);
        var y = List("y");
        var band = List("band");
        var columns = List("columns");
        var series = Text("series", out var seriesSent);
        // Companion fields travel with every row and are never drawn on their own.
        var fields = dataset.FieldsFor(primitive).Where(f => !f.Companion).ToList();
        var companions = dataset.FieldsFor(primitive).Where(f => f.Companion).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var fieldNames = string.Join(", ", fields.Select(f => f.Name));

        if (!present && dataset.DefaultBindings(primitive) is { } defaults)
        {
            x = defaults.X;
            xSent = x is not null;
            y = defaults.Y;
            band = defaults.Band;
            columns = defaults.Columns;
            series = defaults.Series;
            seriesSent = series is not null;
            stacked = defaults.Stacked;
            orientation = defaults.Orientation;
        }

        ReportField? Field(string path, string name)
        {
            var field = fields.FirstOrDefault(f => f.Name == name);
            if (field is null && companions.Contains(name))
                error(path, $"'{name}' comes with every row and cannot be drawn on its own. Fields: {fieldNames}.");
            else if (field is null)
                error(path, $"'{Shorten(name)}' is not a field of {dataset.Name}. Fields: {fieldNames}.");
            return field?.ResolveUnit(useMmol);
        }

        void NotFor(string key, bool sent)
        {
            if (sent)
                error($"{at}.{key}", $"{primitive.WireName()} does not use '{key}'. Remove it.");
        }

        switch (primitive)
        {
            case ReportPrimitive.TimeSeries:
            {
                NotFor("columns", columns is not null);
                NotFor("series", seriesSent);
                var xName = xSent ? x : fields.FirstOrDefault(f => IsAxis(f.Kind))?.Name;
                if (x is not null && Field($"{at}.x", x) is { } xField && !IsAxis(xField.Kind))
                    error($"{at}.x", $"'{x}' is not a field the horizontal axis can show. Send a field of kind 'time', 'time_of_day' or 'offset'.");

                if (y is null || y.Count == 0)
                {
                    if (!malformed.Contains("y"))
                        error($"{at}.y", $"Missing. Send the fields to draw as lines, for example [\"glucose\"]. Fields: {fieldNames}.");
                    return new ReportBindings { X = xName };
                }

                if (y.Distinct().Count() != y.Count)
                    error($"{at}.y", "A field is named twice.");

                var yFields = new List<ReportField>();
                foreach (var name in y)
                {
                    if (Field($"{at}.y", name) is not { } field)
                        continue;
                    if (field.Kind is not (ReportFieldKind.Number or ReportFieldKind.Percent))
                        error($"{at}.y", $"'{name}' is a {Wire(field.Kind)} field. A line needs a number field.");
                    else
                        yFields.Add(field);
                }

                if (yFields.Select(f => f.Unit).Distinct().Count() > 1)
                    error($"{at}.y",
                        $"The fields {string.Join(", ", yFields.Select(f => $"'{f.Name}' ({f.Unit ?? "no unit"})"))} have different units. "
                        + "Put fields of another unit in their own panel.");

                if (band is not null)
                {
                    if (band.Count != 2)
                    {
                        error($"{at}.band", "Send exactly two fields: the lower and the upper edge.");
                    }
                    else
                    {
                        var edges = band.Select(name => Field($"{at}.band", name)).ToList();
                        var unit = yFields.FirstOrDefault()?.Unit;
                        if (edges.All(e => e is not null) && edges.Any(e => e!.Unit != unit || e.Kind is not (ReportFieldKind.Number or ReportFieldKind.Percent)))
                            error($"{at}.band", $"Both edges must be number fields in the unit of 'y' ({unit ?? "no unit"}).");
                    }
                }

                return new ReportBindings { X = xName, Y = y, Band = band };
            }

            // A glucose chart of event cohorts lays out its band as range_bands does.
            case ReportPrimitive.GlucoseChart when dataset.DefaultBindings(ReportPrimitive.GlucoseChart) is not null:
            case ReportPrimitive.RangeBands:
            {
                NotFor("columns", columns is not null);
                var xName = xSent ? x : fields.FirstOrDefault(f => f.Kind is ReportFieldKind.Offset or ReportFieldKind.TimeOfDay)?.Name;
                if (xName is null)
                    error($"{at}.x", "Missing. Send a field of kind 'time_of_day' or 'offset'.");
                else if (Field($"{at}.x", xName) is { } xField && xField.Kind is not (ReportFieldKind.Offset or ReportFieldKind.TimeOfDay))
                    error($"{at}.x", $"'{xName}' is not a field {primitive.WireName()} can lay out. Send a field of kind 'time_of_day' or 'offset'.");

                ReportField? median = null;
                if (y is not { Count: 1 })
                {
                    if (!malformed.Contains("y"))
                        error($"{at}.y", "Send exactly one field: the median line.");
                }
                else if (Field($"{at}.y", y[0]) is { } field)
                {
                    if (field.Kind is not (ReportFieldKind.Number or ReportFieldKind.Percent))
                        error($"{at}.y", $"'{y[0]}' is a {Wire(field.Kind)} field. A line needs a number field.");
                    else
                        median = field;
                }

                if (band is not { Count: 2 or 4 })
                {
                    if (!malformed.Contains("band"))
                        error($"{at}.band", "Send two fields (the band's low and high edges) or four (outer low, inner low, inner high, outer high).");
                }
                else
                {
                    if (band.Distinct().Count() != band.Count)
                        error($"{at}.band", "A field is named twice.");
                    var edges = band.Select(name => Field($"{at}.band", name)).ToList();
                    if (median is not null && edges.All(e => e is not null)
                        && edges.Any(e => e!.Unit != median.Unit || e.Kind is not (ReportFieldKind.Number or ReportFieldKind.Percent)))
                        error($"{at}.band", $"Every edge must be a number field in the unit of 'y' ({median.Unit ?? "no unit"}).");
                }

                if (series is not null && Field($"{at}.series", series) is { } seriesField && seriesField.Kind != ReportFieldKind.Category)
                    error($"{at}.series", $"'{series}' is a {Wire(seriesField.Kind)} field. Send a category field.");

                return new ReportBindings { X = xName, Y = y, Band = band, Series = series };
            }

            case ReportPrimitive.Table:
            case ReportPrimitive.StatTiles:
            case ReportPrimitive.Comparison:
            {
                NotFor("x", xSent);
                NotFor("y", y is not null);
                NotFor("band", band is not null);
                NotFor("series", seriesSent);
                if (columns is { Count: 0 })
                    error($"{at}.columns", "Send at least one field, or leave 'columns' out for every field.");
                foreach (var name in columns ?? [])
                    Field($"{at}.columns", name);
                if (columns is not null && columns.Distinct().Count() != columns.Count)
                    error($"{at}.columns", "A field is named twice.");
                return new ReportBindings { Columns = columns ?? [.. fields.Select(f => f.Name)] };
            }

            case ReportPrimitive.Bar:
            {
                NotFor("columns", columns is not null);
                NotFor("band", band is not null);
                NotFor("series", seriesSent);
                var xName = xSent ? x : fields.FirstOrDefault(f => f.Kind == ReportFieldKind.Category)?.Name;
                if (xName is null)
                    error($"{at}.x", "Missing. Send a field of kind 'category'.");
                else if (Field($"{at}.x", xName) is { } xField && xField.Kind != ReportFieldKind.Category)
                    error($"{at}.x", $"'{xName}' is a {Wire(xField.Kind)} field. A bar's axis needs a category field.");

                if (y is null || y.Count == 0)
                {
                    if (!malformed.Contains("y"))
                        error($"{at}.y", $"Missing. Send the fields to draw as bars. Fields: {fieldNames}.");
                    return new ReportBindings { X = xName, Stacked = stacked, Orientation = orientation ?? ReportBarOrientation.Vertical };
                }

                if (y.Distinct().Count() != y.Count)
                    error($"{at}.y", "A field is named twice.");
                var yFields = new List<ReportField>();
                foreach (var name in y)
                {
                    if (Field($"{at}.y", name) is not { } field)
                        continue;
                    if (field.Kind is not (ReportFieldKind.Number or ReportFieldKind.Percent))
                        error($"{at}.y", $"'{name}' is a {Wire(field.Kind)} field. A bar needs a number field.");
                    else
                        yFields.Add(field);
                }
                if (yFields.Select(f => f.Unit).Distinct().Count() > 1)
                    error($"{at}.y",
                        $"The fields {string.Join(", ", yFields.Select(f => $"'{f.Name}' ({f.Unit ?? "no unit"})"))} have different units. "
                        + "Put fields of another unit in their own panel.");

                return new ReportBindings { X = xName, Y = y, Stacked = stacked ?? false, Orientation = orientation ?? ReportBarOrientation.Vertical };
            }

            case ReportPrimitive.GlucoseChart:
                NotFor("x", xSent);
                NotFor("y", y is not null);
                NotFor("band", band is not null);
                NotFor("columns", columns is not null);
                NotFor("series", seriesSent);
                return new ReportBindings();

            default:
                throw new ArgumentOutOfRangeException(nameof(primitive), primitive, null);
        }
    }

    private static ReportRange? ReadRange(JsonElement element, string at, DateTimeOffset now, Action<string, string> error)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error(at, "Send a range as {\"hours\": N} or {\"from\": \"<ISO date>\", \"to\": \"<ISO date>\"}.");
            return null;
        }

        UnknownKeys(element, at, RangeKeys, error);

        // A key sent as null is absent: the normalised spec render_report returns writes the unused ones so.
        var hasHours = element.TryGetProperty("hours", out var hoursElement) && hoursElement.ValueKind != JsonValueKind.Null;
        var hasFrom = element.TryGetProperty("from", out var fromElement) && fromElement.ValueKind != JsonValueKind.Null;
        var hasTo = element.TryGetProperty("to", out var toElement) && toElement.ValueKind != JsonValueKind.Null;

        if (hasHours)
        {
            if (hasFrom || hasTo)
            {
                error(at, "Send either 'hours', or 'from' and 'to', not both.");
                return null;
            }
            if (hoursElement.ValueKind != JsonValueKind.Number || !hoursElement.TryGetInt32(out var hours)
                || hours < 1 || hours > ReportSpec.MaxRangeHours)
            {
                error($"{at}.hours", $"Send a whole number of hours, 1 to {ReportSpec.MaxRangeHours}.");
                return null;
            }
            return new ReportRange { Hours = hours };
        }

        if (!hasFrom || !hasTo)
        {
            error(at, "Send 'hours', or both 'from' and 'to'.");
            return null;
        }

        var latest = now.AddDays(1);
        var from = Date(fromElement, $"{at}.from", latest, error);
        var to = Date(toElement, $"{at}.to", latest, error);
        if (from is null || to is null)
            return null;
        if (from > now)
        {
            error($"{at}.from", "'from' must not be after now: there is no data after now.");
            return null;
        }
        if (to <= from)
        {
            error($"{at}.to", "'to' must be after 'from'.");
            return null;
        }

        return new ReportRange { From = from, To = to };
    }

    private static DateTimeOffset? Date(JsonElement element, string at, DateTimeOffset latest, Action<string, string> error)
    {
        if (element.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            error(at, "Send an ISO 8601 date and time, for example 2026-10-29T06:00:00Z.");
            return null;
        }

        var utc = date.ToUniversalTime();
        if (utc < EarliestDate || utc > latest)
        {
            error(at, $"Send a date from {EarliestDate:yyyy-MM-dd} to one day after now.");
            return null;
        }
        return utc;
    }

    private static string? OptionalString(JsonElement owner, string key, string at, int maxLength, Action<string, string> error)
    {
        if (!owner.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
        {
            error(at, "Send text.");
            return null;
        }
        var text = value.GetString()!.Trim();
        if (text.Length > maxLength)
        {
            error(at, $"Use {maxLength} characters or fewer.");
            return null;
        }
        return text.Length == 0 ? null : text;
    }

    private static void UnknownKeys(JsonElement element, string at, string[] known, Action<string, string> error)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name, StringComparer.Ordinal))
                error($"{at}.{Shorten(property.Name)}", $"Not a known key. Keys here: {string.Join(", ", known)}.");
        }
    }

    /// <summary>A value the model sent, cut short enough to quote back.</summary>
    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "...";

    private static string NextFreeId(HashSet<string> taken, int index)
    {
        for (var n = index + 1; ; n++)
        {
            var id = $"p{n}";
            if (!taken.Contains(id))
                return id;
        }
    }

    private static string PrimitiveList(IEnumerable<ReportPrimitive> primitives) =>
        string.Join(", ", primitives.Select(p => p.WireName()));

    /// <summary>Whether a field of <paramref name="kind"/> can lie along a time_series' horizontal axis.</summary>
    private static bool IsAxis(ReportFieldKind kind) =>
        kind is ReportFieldKind.Time or ReportFieldKind.TimeOfDay or ReportFieldKind.Offset;

    private static string Wire(ReportFieldKind kind) =>
        JsonSerializer.Serialize(kind).Trim('"');

    internal static string Describe(TimeSpan span) =>
        span.TotalHours <= 48
            ? $"{span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} hours"
            : $"{span.TotalDays.ToString("0.#", CultureInfo.InvariantCulture)} days";

    [GeneratedRegex(ReportSpec.IdPattern)]
    private static partial Regex IdPattern();
}
