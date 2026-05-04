namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// Replay output: a single (timestamp, value) sample for a numeric fact derived from
/// <see cref="SensorContext"/>. Emitted alongside the per-leaf truth log so the rule
/// editor's sidebar can annotate comparison-style leaves with the underlying value
/// at the playhead (e.g. "Site age &lt; 3d · 1.2d").
/// </summary>
/// <remarks>
/// Compressed at write time — only the points where the rounded display value changed
/// are emitted. The first observed tick always emits a baseline.
/// </remarks>
/// <param name="AtMs">Replay tick instant in Unix milliseconds.</param>
/// <param name="Value">Fact value at that tick, in the unit declared by the source
/// property's <see cref="ReplayFactAttribute"/>.</param>
public sealed record FactSnapshotPoint(long AtMs, decimal Value);

/// <summary>
/// Marks a <see cref="SensorContext"/> property as a numeric fact replay should snapshot
/// each tick. Adding a new fact is a one-line change: drop the attribute on the property
/// and replay picks it up automatically — no parallel registry to maintain. The
/// <see cref="Key"/> is the single source of truth for the wire name; the FE's
/// leaf-to-fact mapping references the same string.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ReplayFactAttribute : Attribute
{
    /// <summary>Snake_case wire key the FE looks up in <c>FactTimelines</c>. Keep stable;
    /// renaming is a wire break.</summary>
    public string Key { get; }

    /// <summary>Decimal places to round to before change-detection. Mirrors the precision
    /// the rule sidebar will display so the FE never sees jitter the user can't perceive.</summary>
    public int Decimals { get; }

    /// <summary>How to project the property's stored value to the wire value. Direct for
    /// numeric properties; the time-since variants take a <see cref="DateTime"/>? property
    /// and emit minutes/hours/days between that timestamp and the current replay tick.</summary>
    public ReplayFactConversion Conversion { get; }

    public ReplayFactAttribute(
        string key,
        int decimals = 2,
        ReplayFactConversion conversion = ReplayFactConversion.Direct)
    {
        Key = key;
        Decimals = decimals;
        Conversion = conversion;
    }
}

/// <summary>
/// How a <see cref="ReplayFactAttribute"/>-tagged property's value is projected to its
/// numeric wire form. <see cref="Direct"/> is for <c>decimal?</c> properties; the
/// time-since variants take a <c>DateTime?</c> property and compute the elapsed time
/// from that timestamp to the current replay tick.
/// </summary>
public enum ReplayFactConversion
{
    /// <summary>Property is <c>decimal?</c>; emit the value as-is.</summary>
    Direct,
    /// <summary>Property is <c>DateTime?</c>; emit minutes between value and current tick.</summary>
    MinutesSinceNow,
    /// <summary>Property is <c>DateTime?</c>; emit hours between value and current tick.</summary>
    HoursSinceNow,
    /// <summary>Property is <c>DateTime?</c>; emit days between value and current tick.</summary>
    DaysSinceNow,
}
