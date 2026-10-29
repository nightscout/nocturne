using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Nocturne.API.Mcp.Reports;

/// <summary>One thing wrong with a spec: where (<see cref="Path"/>, e.g. <c>panels[1].bindings.y</c>) and what to change.</summary>
internal sealed record ReportSpecError(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

internal sealed record ReportSpecValidation(ReportSpec? Spec, IReadOnlyList<ReportSpecError> Errors)
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
/// nor the choices, and the model needs both to repair the spec.
/// </remarks>
internal sealed partial class ReportSpecValidator(IReportDatasetRegistry registry)
{
    private static readonly JsonSerializerOptions ParameterOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly string[] SpecKeys = ["version", "title", "range", "panels"];
    private static readonly string[] PanelKeys = ["id", "title", "dataset", "primitive", "bindings", "range"];
    private static readonly string[] RangeKeys = ["hours", "from", "to"];
    private static readonly string[] BindingKeys = ["x", "y", "band", "columns"];

    public ReportSpecValidation Validate(JsonElement spec)
    {
        var errors = new List<ReportSpecError>();
        void Error(string path, string message) => errors.Add(new ReportSpecError(path, message));

        if (spec.ValueKind != JsonValueKind.Object)
        {
            Error("spec", "Send the spec as a JSON object with 'version' and 'panels'.");
            return new ReportSpecValidation(null, errors);
        }

        UnknownKeys(spec, "spec", SpecKeys, Error);

        if (!spec.TryGetProperty("version", out var version) || version.ValueKind == JsonValueKind.Null)
            Error("version", $"Missing. Send \"version\": {ReportSpec.CurrentVersion}.");
        else if (!version.TryGetInt32(out var v) || v != ReportSpec.CurrentVersion)
            Error("version", $"Version {version.GetRawText()} is not supported. Send {ReportSpec.CurrentVersion}.");

        var title = OptionalString(spec, "title", "title", 200, Error);
        var reportRange = spec.TryGetProperty("range", out var r) ? ReadRange(r, "range", Error) : null;

        var panels = new List<ReportPanelSpec>();
        if (!spec.TryGetProperty("panels", out var panelsElement) || panelsElement.ValueKind != JsonValueKind.Array)
        {
            Error("panels", "Missing. Send a list of 1 to 6 panels.");
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
                if (ReadPanel(panel, index, reportRange, ids, Error) is { } read)
                    panels.Add(read);
                index++;
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
            errors);
    }

    private ReportPanelSpec? ReadPanel(
        JsonElement panel,
        int index,
        ReportRange? reportRange,
        HashSet<string> ids,
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

        var id = OptionalString(panel, "id", $"{at}.id", 32, Error);
        if (id is not null && !IdPattern().IsMatch(id))
            Error($"{at}.id", "Use only letters, digits, '-' and '_'.");
        if (id is not null && !ids.Add(id))
            Error($"{at}.id", $"'{id}' is already the id of another panel. Panel ids must be unique.");

        var title = OptionalString(panel, "title", $"{at}.title", 200, Error);

        var dataset = ReadDataset(panel, at, Error, out var parameters);

        ReportPrimitive? primitive = null;
        if (!panel.TryGetProperty("primitive", out var p) || p.ValueKind != JsonValueKind.String)
        {
            Error($"{at}.primitive", $"Missing. Send one of: {PrimitiveList(ReportPrimitives.All)}.");
        }
        else if (ReportPrimitives.Parse(p.GetString()) is not { } parsed)
        {
            Error($"{at}.primitive", $"'{p.GetString()}' is not a primitive. Send one of: {PrimitiveList(ReportPrimitives.All)}.");
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

        var range = panel.TryGetProperty("range", out var pr) ? ReadRange(pr, $"{at}.range", Error) : null;
        range ??= reportRange;
        if (dataset is not null && primitive is { } drawn)
        {
            range ??= new ReportRange { Hours = (int)dataset.DefaultRange.TotalHours };
            var span = range.Hours is { } hours ? TimeSpan.FromHours(hours) : range.To!.Value - range.From!.Value;
            var max = dataset.MaxRange(drawn);
            if (span > max)
                Error($"{at}.range",
                    $"{dataset.Name} drawn as {drawn.WireName()} covers at most {Describe(max)}; this range is {Describe(span)}. "
                    + "Shorten it, or split it into more than one panel.");
        }

        ReportBindings? bindings = null;
        if (dataset is not null && primitive is { } kind)
        {
            panel.TryGetProperty("bindings", out var b);
            bindings = ReadBindings(b, $"{at}.bindings", dataset, kind, Error);
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
            error($"{at}.dataset.name", $"'{name}' is not a dataset. Send one of: {names}.");
            return null;
        }

        var body = JsonObject.Create(element)!;
        body.Remove("name");
        try
        {
            parameters = (ReportDatasetParameters?)body.Deserialize(dataset.ParametersType, ParameterOptions);
        }
        catch (JsonException ex)
        {
            error($"{at}.dataset", $"The parameters of {name} are not valid: {ex.Message} Call describe_dataset for its parameters.");
            return dataset;
        }

        foreach (var (field, message) in dataset.Validate(parameters!))
            error($"{at}.dataset.{field}", message);

        return dataset;
    }

    private static ReportBindings ReadBindings(
        JsonElement element,
        string at,
        IReportDataset dataset,
        ReportPrimitive primitive,
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

        IReadOnlyList<string>? List(string key)
        {
            if (!present || !element.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
                return null;
            if (value.ValueKind == JsonValueKind.String)
                return [value.GetString()!];
            if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String))
                return [.. value.EnumerateArray().Select(v => v.GetString()!)];
            error($"{at}.{key}", "Send a field name or a list of field names.");
            return null;
        }

        var x = List("x");
        var y = List("y");
        var band = List("band");
        var columns = List("columns");
        var fieldNames = string.Join(", ", dataset.Fields.Select(f => f.Name));

        ReportField? Field(string path, string name)
        {
            var field = dataset.Fields.FirstOrDefault(f => f.Name == name);
            if (field is null)
                error(path, $"'{name}' is not a field of {dataset.Name}. Fields: {fieldNames}.");
            return field;
        }

        void NotFor(string key, IReadOnlyList<string>? value)
        {
            if (value is not null)
                error($"{at}.{key}", $"{primitive.WireName()} does not use '{key}'. Remove it.");
        }

        switch (primitive)
        {
            case ReportPrimitive.TimeSeries:
            {
                NotFor("columns", columns);
                var xName = x is null ? dataset.Fields.FirstOrDefault(f => f.Kind == ReportFieldKind.Time)?.Name : x.Single();
                if (x is { Count: not 1 })
                    error($"{at}.x", "Send one time field.");
                else if (xName is not null && Field($"{at}.x", xName) is { } xField && xField.Kind != ReportFieldKind.Time)
                    error($"{at}.x", $"'{xName}' is not a time field. Send a field of kind 'time'.");

                if (y is null || y.Count == 0)
                {
                    error($"{at}.y", $"Missing. Send the field or fields to draw as lines. Fields: {fieldNames}.");
                    return new ReportBindings { X = xName };
                }

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

            case ReportPrimitive.Table:
            case ReportPrimitive.StatTiles:
            {
                NotFor("x", x);
                NotFor("y", y);
                NotFor("band", band);
                if (columns is { Count: 0 })
                    error($"{at}.columns", "Send at least one field, or leave 'columns' out for every field.");
                foreach (var name in columns ?? [])
                    Field($"{at}.columns", name);
                if (columns is not null && columns.Distinct().Count() != columns.Count)
                    error($"{at}.columns", "A field is named twice.");
                return new ReportBindings { Columns = columns ?? [.. dataset.Fields.Select(f => f.Name)] };
            }

            case ReportPrimitive.GlucoseChart:
                NotFor("x", x);
                NotFor("y", y);
                NotFor("band", band);
                NotFor("columns", columns);
                return new ReportBindings();

            default:
                throw new ArgumentOutOfRangeException(nameof(primitive), primitive, null);
        }
    }

    private static ReportRange? ReadRange(JsonElement element, string at, Action<string, string> error)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error(at, "Send a range as {\"hours\": N} or {\"from\": \"<ISO date>\", \"to\": \"<ISO date>\"}.");
            return null;
        }

        UnknownKeys(element, at, RangeKeys, error);

        var hasHours = element.TryGetProperty("hours", out var hoursElement);
        var hasFrom = element.TryGetProperty("from", out var fromElement);
        var hasTo = element.TryGetProperty("to", out var toElement);

        if (hasHours)
        {
            if (hasFrom || hasTo)
            {
                error(at, "Send either 'hours', or 'from' and 'to', not both.");
                return null;
            }
            if (!hoursElement.TryGetInt32(out var hours) || hours < 1)
            {
                error($"{at}.hours", "Send a whole number of hours, 1 or more.");
                return null;
            }
            return new ReportRange { Hours = hours };
        }

        if (!hasFrom || !hasTo)
        {
            error(at, "Send 'hours', or both 'from' and 'to'.");
            return null;
        }

        var from = Date(fromElement, $"{at}.from", error);
        var to = Date(toElement, $"{at}.to", error);
        if (from is null || to is null)
            return null;
        if (to <= from)
        {
            error($"{at}.to", "'to' must be after 'from'.");
            return null;
        }

        return new ReportRange { From = from, To = to };
    }

    private static DateTimeOffset? Date(JsonElement element, string at, Action<string, string> error)
    {
        if (element.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return date.ToUniversalTime();
        error(at, "Send an ISO 8601 date and time, for example 2026-10-29T06:00:00Z.");
        return null;
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
                error($"{at}.{property.Name}", $"Not a known key. Keys here: {string.Join(", ", known)}.");
        }
    }

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

    private static string Wire(ReportFieldKind kind) =>
        JsonSerializer.Serialize(kind).Trim('"');

    internal static string Describe(TimeSpan span) =>
        span.TotalHours <= 48
            ? $"{span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} hours"
            : $"{span.TotalDays.ToString("0.#", CultureInfo.InvariantCulture)} days";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex IdPattern();
}
