namespace Nocturne.Connectors.Tandem.EventParser;

/// <summary>Primitive on-the-wire field types used by the pump event schema (all big-endian).</summary>
public enum TandemFieldType
{
    UInt8,
    Int8,
    UInt16,
    Int16,
    UInt32,
    Float32,
}

/// <summary>The kind of post-decode transform applied to a raw field value.</summary>
public enum TandemTransformKind
{
    /// <summary>Maps the raw integer to a named member (e.g. completion status).</summary>
    Enum,

    /// <summary>Maps the raw integer through a named static dictionary (alerts / alarms / dalerts).</summary>
    Dictionary,

    /// <summary>Interprets the raw integer as a bitmask, yielding the names of the set bits.</summary>
    Bitmask,

    /// <summary>Multiplies the raw integer by a fixed factor (e.g. milliunits, 0.1 mg/dL/min).</summary>
    Ratio,

    /// <summary>Special battery-charge-percent computation from MSB/LSB raw fields.</summary>
    BatteryChargePercent,
}

/// <summary>A single transform attached to a field in the event schema.</summary>
public sealed class TandemFieldTransform
{
    public required TandemTransformKind Kind { get; init; }

    /// <summary>For <see cref="TandemTransformKind.Enum"/> / <see cref="TandemTransformKind.Bitmask"/>: raw value/bit index → name.</summary>
    public IReadOnlyDictionary<int, string>? Map { get; init; }

    /// <summary>For <see cref="TandemTransformKind.Dictionary"/>: the named static table ("alerts", "alarms", "dalerts").</summary>
    public string? DictionaryName { get; init; }

    /// <summary>For <see cref="TandemTransformKind.Ratio"/>: the multiplier applied to the raw integer.</summary>
    public double RatioFactor { get; init; }
}

/// <summary>One field within a pump event payload.</summary>
public sealed class TandemFieldDefinition
{
    /// <summary>The original schema field name (e.g. "CompletionStatus", "EGV TimeStamp").</summary>
    public required string Name { get; init; }

    public required TandemFieldType Type { get; init; }

    /// <summary>Byte offset within the 16-byte payload (after the 10-byte header).</summary>
    public required int Offset { get; init; }

    public string? Unit { get; init; }

    public IReadOnlyList<TandemFieldTransform> Transforms { get; init; } = [];
}

/// <summary>The schema for one pump event id.</summary>
public sealed class TandemEventDefinition
{
    public required int Id { get; init; }

    /// <summary>The raw LID name, e.g. "LID_BOLUS_COMPLETED".</summary>
    public required string Name { get; init; }

    public required IReadOnlyList<TandemFieldDefinition> Fields { get; init; }
}
