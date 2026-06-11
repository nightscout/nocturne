using System.Reflection;
using System.Text.Json;

namespace Nocturne.Connectors.Tandem.EventParser;

/// <summary>
/// Loads and merges the embedded pump-event schema (<c>tandem-events.json</c> plus
/// <c>tandem-custom-events.json</c>) into a lookup of event id → <see cref="TandemEventDefinition"/>.
/// The schema is the same data-driven definition used by <c>tconnectsync</c>; decoding it at runtime
/// keeps a single source of truth rather than transcribing ~5,800 lines of generated event classes.
/// </summary>
public static class TandemEventCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<int, TandemEventDefinition>> LazyDefinitions =
        new(Load);

    /// <summary>Event id → definition, for every event in the merged schema.</summary>
    public static IReadOnlyDictionary<int, TandemEventDefinition> Definitions => LazyDefinitions.Value;

    private static IReadOnlyDictionary<int, TandemEventDefinition> Load()
    {
        var merged = new Dictionary<int, TandemEventDefinition>();

        // custom events are merged last so they can add to (or override) the base schema, matching
        // tconnectsync's build_events.py which updates the base dict with the custom dict.
        foreach (var resource in new[] { "tandem-events.json", "tandem-custom-events.json" })
        foreach (var def in ParseResource(resource))
            merged[def.Id] = def;

        return merged;
    }

    private static IEnumerable<TandemEventDefinition> ParseResource(string fileName)
    {
        var assembly = typeof(TandemEventCatalog).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded event schema '{fileName}' not found");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Could not open embedded event schema '{resourceName}'");
        using var doc = JsonDocument.Parse(stream);

        if (!doc.RootElement.TryGetProperty("events", out var events))
            yield break;

        foreach (var entry in events.EnumerateObject().Where(e => int.TryParse(e.Name, out _)))
        {
            var id = int.Parse(entry.Name);
            var name = entry.Value.GetProperty("name").GetString() ?? $"LID_{id}";
            var fields = new List<TandemFieldDefinition>();

            if (entry.Value.TryGetProperty("data", out var data))
                foreach (var field in data.EnumerateObject())
                    fields.Add(ParseField(field.Name, field.Value));

            yield return new TandemEventDefinition { Id = id, Name = name, Fields = fields };
        }
    }

    private static TandemFieldDefinition ParseField(string name, JsonElement element)
    {
        var type = ParseType(element.GetProperty("type").GetString());
        var offset = element.GetProperty("offset").GetInt32();
        var unit = element.TryGetProperty("uom", out var uom) ? uom.GetString() : null;

        List<TandemFieldTransform> transforms = element.TryGetProperty("transform", out var transformArray)
            ? transformArray.EnumerateArray()
                .Select(ParseTransform)
                .Where(parsed => parsed != null)
                .Select(parsed => parsed!)
                .ToList()
            : [];

        return new TandemFieldDefinition
        {
            Name = name,
            Type = type,
            Offset = offset,
            Unit = unit,
            Transforms = transforms,
        };
    }

    private static TandemFieldTransform? ParseTransform(JsonElement transform)
    {
        // Each transform is a 2-element array: [kind, argument].
        if (transform.ValueKind != JsonValueKind.Array || transform.GetArrayLength() < 2)
            return null;

        var kind = transform[0].GetString();
        var arg = transform[1];

        return kind switch
        {
            "enum" => new TandemFieldTransform { Kind = TandemTransformKind.Enum, Map = ParseMap(arg) },
            "bitmask" => new TandemFieldTransform { Kind = TandemTransformKind.Bitmask, Map = ParseMap(arg) },
            "dictionary" => new TandemFieldTransform
            {
                Kind = TandemTransformKind.Dictionary,
                DictionaryName = arg.GetString(),
            },
            "ratio" => new TandemFieldTransform
            {
                Kind = TandemTransformKind.Ratio,
                RatioFactor = arg.GetDouble(),
            },
            "battery_charge_percent" => new TandemFieldTransform
            {
                Kind = TandemTransformKind.BatteryChargePercent,
            },
            _ => null,
        };
    }

    private static IReadOnlyDictionary<int, string> ParseMap(JsonElement arg)
    {
        if (arg.ValueKind != JsonValueKind.Object)
            return new Dictionary<int, string>();

        return arg.EnumerateObject()
            .Select(member => (Parsed: int.TryParse(member.Name, out var key), Key: key, Value: member.Value.GetString()))
            .Where(x => x is { Parsed: true, Value: not null })
            .ToDictionary(x => x.Key, x => x.Value!);
    }

    private static TandemFieldType ParseType(string? type) => type switch
    {
        "uint8" => TandemFieldType.UInt8,
        "int8" => TandemFieldType.Int8,
        "uint16" => TandemFieldType.UInt16,
        "int16" => TandemFieldType.Int16,
        "uint32" => TandemFieldType.UInt32,
        "float32" => TandemFieldType.Float32,
        _ => throw new InvalidOperationException($"Unknown pump-event field type '{type}'"),
    };
}
