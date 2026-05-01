using System.Globalization;
using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a time-of-day condition by checking whether the current local time within the
/// configured IANA timezone falls inside the half-open window <c>[From, To)</c>.
/// </summary>
/// <remarks>
/// When <see cref="TimeOfDayCondition.From"/> &gt; <see cref="TimeOfDayCondition.To"/> the
/// window is treated as wrapping past midnight (e.g. 22:00–06:00). A null
/// <see cref="TimeOfDayCondition.Timezone"/> is treated as UTC. An unknown or unparseable
/// timezone id, or unparseable time strings, yields <see langword="false"/> — alert
/// evaluation is best-effort and a malformed rule must not crash the orchestrator.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
public class TimeOfDayEvaluator : IConditionEvaluator
{

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initialises a new <see cref="TimeOfDayEvaluator"/>.
    /// </summary>
    /// <param name="timeProvider">Abstraction for the current UTC time, enabling deterministic unit tests.</param>
    public TimeOfDayEvaluator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.TimeOfDay;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="TimeOfDayCondition"/>.</param>
    /// <param name="context">Current sensor context (unused for this evaluator).</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        var condition = JsonSerializer.Deserialize<TimeOfDayCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        if (!TimeOnly.TryParseExact(condition.From, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
            !TimeOnly.TryParseExact(condition.To, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
        {
            return Task.FromResult(false);
        }

        TimeZoneInfo tz;
        if (string.IsNullOrEmpty(condition.Timezone))
        {
            tz = TimeZoneInfo.Utc;
        }
        else
        {
            try
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById(condition.Timezone);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);
        var current = TimeOnly.FromDateTime(localNow);

        // Half-open [From, To). When From > To, the window wraps midnight.
        var inWindow = from <= to
            ? current >= from && current < to
            : current >= from || current < to;

        return Task.FromResult(inWindow);
    }
}
