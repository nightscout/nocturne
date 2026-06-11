namespace Nocturne.Connectors.Tandem.EventParser;

/// <summary>
/// One decoded field value within a pump event. Carries the raw integer (for enum / bitmask /
/// dictionary lookups), the numeric value (raw, or raw × factor for ratio fields, or the float),
/// the resolved enum/dictionary name, and the names of any set bitmask bits.
/// </summary>
public sealed class TandemFieldValue
{
    /// <summary>The numeric value: the float, the raw integer, or raw × ratio factor.</summary>
    public required double Numeric { get; init; }

    /// <summary>The raw integer value (0 for float fields).</summary>
    public required long RawInteger { get; init; }

    /// <summary>The resolved name for an enum or dictionary field, or null if unmapped.</summary>
    public string? Name { get; init; }

    /// <summary>The names of the set bits for a bitmask field.</summary>
    public IReadOnlyList<string> Bits { get; init; } = [];
}

/// <summary>
/// A decoded Tandem pump event: its id/name, sequence number, raw timestamp, and decoded fields
/// keyed by their original schema name. Field accessors mirror the typed properties that
/// <c>tconnectsync</c> generates per event class.
/// </summary>
public sealed class TandemPumpEvent
{
    public required int Id { get; init; }

    /// <summary>The raw LID name, or "RawEvent" for an unrecognised id.</summary>
    public required string Name { get; init; }

    /// <summary>The 4-bit source identifier from the event header.</summary>
    public required int Source { get; init; }

    public required uint SeqNum { get; init; }

    /// <summary>Seconds since the Tandem epoch (2008-01-01), as stored in the event header.</summary>
    public required long RawTimestampSeconds { get; init; }

    /// <summary>Whether the event id was found in the schema (and therefore has decoded fields).</summary>
    public bool IsKnown { get; init; }

    public IReadOnlyDictionary<string, TandemFieldValue> Fields { get; init; } =
        new Dictionary<string, TandemFieldValue>();

    public bool Has(string field) => Fields.ContainsKey(field);

    /// <summary>The numeric value of a field (float, raw integer, or raw × ratio), or null if absent.</summary>
    public double? Num(string field) => Fields.TryGetValue(field, out var v) ? v.Numeric : null;

    /// <summary>The raw integer value of a field, or null if absent.</summary>
    public long? Raw(string field) => Fields.TryGetValue(field, out var v) ? v.RawInteger : null;

    /// <summary>The resolved enum/dictionary name of a field, or null if absent or unmapped.</summary>
    public string? EnumName(string field) => Fields.TryGetValue(field, out var v) ? v.Name : null;

    /// <summary>The set-bit names of a bitmask field (empty if absent).</summary>
    public IReadOnlyList<string> Bits(string field) =>
        Fields.TryGetValue(field, out var v) ? v.Bits : [];
}
