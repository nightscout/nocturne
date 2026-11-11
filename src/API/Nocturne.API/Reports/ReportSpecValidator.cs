using System.Buffers;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Nocturne.API.Reports;

/// <summary>One thing wrong with a spec: where (<see cref="Path"/>, e.g. <c>panels[1].bindings.y</c>) and what to change.</summary>
internal sealed record ReportSpecError(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

internal sealed record ReportSpecValidation(ReportSpec? Spec, IReadOnlyList<ReportSpecError> Errors)
{
    public bool IsValid => Errors.Count == 0 && Spec is not null;

    /// <summary>The dataset reads a run of the spec makes, counted by the runner's read key; the cap is on this.</summary>
    public int Reads { get; init; }
}

/// <summary>
/// Reads a report spec into a normalised <see cref="ReportSpec"/>,
/// or lists every error in it. Normalised: version stamped, panel, track and layer ids assigned, each
/// panel's range, each layer's mark and bindings filled from the report's, the dataset's or the op's
/// defaults, and each layer's axis, unit and side worked out.
/// </summary>
/// <remarks>
/// It reads the JSON itself rather than deserialising the polymorphic type: the serializer stops
/// at the first unknown dataset name or enum value, with a message that names neither the panel
/// nor the choices and does name .NET types, and whoever repairs the spec needs the panel and the
/// choices. Every value is checked for its JSON kind before it is read, so a malformed spec
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

    private static readonly string[] RangeKeys = ["hours", "from", "to"];
    /// <summary>The dataset <paramref name="owner"/>'s <c>dataset</c> names, with its parameters read; null when it names none.</summary>
    private IReportDataset? ReadDataset(
        JsonElement owner,
        string at,
        Action<string, string> error,
        out ReportDatasetParameters? parameters)
    {
        parameters = null;
        var names = string.Join(", ", registry.All.Select(d => d.Name));

        if (!owner.TryGetProperty("dataset", out var element) || element.ValueKind != JsonValueKind.Object)
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
            error(path, message);
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
                (null, _) => "Send a value of the kind this parameter takes.",
                (_, null) => "Not a known key.",
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
            var t when t == typeof(Guid) => "a food id",
            var t when t == typeof(DateOnly) => "a date, yyyy-MM-dd",
            var t when t.IsEnum => "one of: " + string.Join(", ", EnumNames(t)),
            var t when t != typeof(string) && ElementType(t) is { } element => $"a list, each item {Expected(element)}",
            var t when t.IsClass => "an object with this parameter's keys",
            _ => "a value of the kind this parameter takes",
        };

    /// <summary>An enum's values as they are sent: their <see cref="JsonStringEnumMemberNameAttribute"/>, else their names.</summary>
    private static IEnumerable<string> EnumNames(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? f.Name);

    /// <summary>The object without <paramref name="skip"/>, written property by property so a repeated key cannot throw.</summary>
    private static byte[] WithoutName(JsonElement element, params string[] skip)
    {
        if (skip.Length == 0)
            skip = ["name"];
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject())
            {
                if (skip.Contains(property.Name, StringComparer.Ordinal))
                    continue;
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
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

        // A key sent as null is absent: the normalised spec writes the unused ones so.
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

    /// <summary>A value as it was sent, cut short enough to quote back.</summary>
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

    internal static string Describe(TimeSpan span) =>
        span.TotalHours <= 48
            ? $"{span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} hours"
            : $"{span.TotalDays.ToString("0.#", CultureInfo.InvariantCulture)} days";

    [GeneratedRegex(ReportSpec.IdPattern)]
    private static partial Regex IdPattern();

    [GeneratedRegex(ReportSpec.GroupPattern)]
    private static partial Regex GroupPattern();
}
