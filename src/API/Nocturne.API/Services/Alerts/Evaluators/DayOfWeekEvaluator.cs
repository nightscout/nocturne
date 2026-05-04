using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a <see cref="DayOfWeekCondition"/> by checking whether the current local day,
/// computed from <see cref="SensorContext.TenantTimeZoneId"/>, falls into the configured
/// <see cref="DayOfWeekCondition.Days"/> set.
/// </summary>
/// <remarks>
/// Falls back to UTC when <see cref="SensorContext.TenantTimeZoneId"/> is null or unparseable
/// — matches <see cref="TimeOfDayEvaluator"/>'s "best effort, never throw" stance. Crossing
/// DST is naturally handled by <see cref="TimeZoneInfo.ConvertTimeFromUtc(DateTime, TimeZoneInfo)"/>.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
public sealed class DayOfWeekEvaluator : IConditionEvaluator
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new <see cref="DayOfWeekEvaluator"/>.</summary>
    public DayOfWeekEvaluator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.DayOfWeek;

    /// <inheritdoc/>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        var condition = JsonSerializer.Deserialize<DayOfWeekCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null || condition.Days is null || condition.Days.Count == 0)
            return Task.FromResult(false);

        TimeZoneInfo tz;
        if (string.IsNullOrEmpty(context.TenantTimeZoneId))
        {
            tz = TimeZoneInfo.Utc;
        }
        else
        {
            try
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById(context.TenantTimeZoneId);
            }
            catch
            {
                tz = TimeZoneInfo.Utc;
            }
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);
        return Task.FromResult(condition.Days.Contains(localNow.DayOfWeek));
    }
}
