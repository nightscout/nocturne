namespace Nocturne.Core.Models.Alerts.Conditions;

/// <summary>
/// Glucose-bucket alert condition. True when the current glucose reading falls into any of
/// <see cref="Buckets"/>. The bucket itself is computed by the context enricher from the
/// active <see cref="V4.TargetRangeEntry"/> boundaries; this evaluator only reads
/// <c>SensorContext.GlucoseBucket</c>.
/// </summary>
/// <param name="Buckets">The set of buckets that satisfy the condition.</param>
public record GlucoseBucketCondition(List<GlucoseBucket> Buckets);
