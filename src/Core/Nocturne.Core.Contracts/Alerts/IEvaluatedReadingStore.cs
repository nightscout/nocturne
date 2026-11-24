using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// The fields of a canonical reading an alert context is built from, which tell one evaluated
/// reading from another: a corrected value or trend at an unchanged timestamp is a different
/// reading, and a same-valued replacement row is not.
/// </summary>
public readonly record struct EvaluatedReading(DateTime Timestamp, double Mgdl, double? TrendRate)
{
    public static EvaluatedReading Of(SensorGlucose reading) => new(reading.Timestamp, reading.Mgdl, reading.TrendRate);
}

/// <summary>
/// Per tenant, the newest reading every enabled rule was last evaluated against by a pass that
/// completed (docs/alerts/engine-semantics.md §5.2). Durable, so a process that starts after
/// another died knows which reading still has no completed evaluation.
/// </summary>
public interface IEvaluatedReadingStore
{
    /// <summary>The current tenant's reading, or null when none was recorded.</summary>
    Task<EvaluatedReading?> GetAsync(CancellationToken ct);

    /// <summary>Records <paramref name="reading"/> as the current tenant's, in one statement.</summary>
    Task RecordAsync(EvaluatedReading reading, CancellationToken ct);
}
