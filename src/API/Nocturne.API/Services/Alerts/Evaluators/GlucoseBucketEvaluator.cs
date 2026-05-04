using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Alerts.Conditions;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a <see cref="GlucoseBucketCondition"/> against the precomputed
/// <see cref="SensorContext.GlucoseBucket"/> on the context.
/// </summary>
/// <remarks>
/// Bucket assignment is performed once per evaluation pass by the
/// <see cref="SensorContextEnricher"/> using the active <see cref="Nocturne.Core.Models.V4.TargetRangeEntry"/>
/// boundaries. This evaluator just performs a set membership check, so it is purely
/// in-memory and synchronous (returning a completed task).
/// Returns <see langword="false"/> when no bucket can be resolved (no glucose reading,
/// or no target range schedule for the tenant).
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
public sealed class GlucoseBucketEvaluator : IConditionEvaluator
{
    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.GlucoseBucket;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="GlucoseBucketCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.GlucoseBucket"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        if (context.GlucoseBucket is not { } bucket)
            return Task.FromResult(false);

        var condition = JsonSerializer.Deserialize<GlucoseBucketCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null || condition.Buckets is null || condition.Buckets.Count == 0)
            return Task.FromResult(false);

        return Task.FromResult(condition.Buckets.Contains(bucket));
    }
}
