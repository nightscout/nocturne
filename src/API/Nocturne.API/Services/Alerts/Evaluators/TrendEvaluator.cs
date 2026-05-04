using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a trend bucket condition by comparing the configured
/// <see cref="TrendCondition.Bucket"/> wire string against
/// <see cref="SensorContext.TrendBucket"/>.
/// </summary>
/// <remarks>
/// The comparison is case-insensitive against the snake_case wire form encoded by
/// <see cref="JsonStringEnumMemberNameAttribute"/> on <see cref="TrendBucket"/>. Returns
/// <see langword="false"/> when <see cref="SensorContext.TrendBucket"/> is null or the
/// configured bucket does not map to a known enum member.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
public class TrendEvaluator : IConditionEvaluator
{

    private static readonly Dictionary<TrendBucket, string> BucketWire = BuildWire();

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.Trend;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="TrendCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.TrendBucket"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        if (context.TrendBucket is null)
            return Task.FromResult(false);

        var condition = JsonSerializer.Deserialize<TrendCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null || string.IsNullOrEmpty(condition.Bucket))
            return Task.FromResult(false);

        var actual = BucketWire[context.TrendBucket.Value];
        return Task.FromResult(string.Equals(actual, condition.Bucket, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<TrendBucket, string> BuildWire()
    {
        var values = Enum.GetValues<TrendBucket>();
        var map = new Dictionary<TrendBucket, string>(values.Length);
        foreach (var value in values)
        {
            var member = typeof(TrendBucket).GetMember(value.ToString()).FirstOrDefault();
            var attr = member?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>();
            map[value] = attr?.Name ?? value.ToString().ToLowerInvariant();
        }
        return map;
    }
}
