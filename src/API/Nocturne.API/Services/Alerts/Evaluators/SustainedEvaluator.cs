using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a sustained-duration wrapper, firing only once its child has held continuously
/// for at least <see cref="SustainedCondition.Minutes"/>.
/// </summary>
/// <remarks>
/// State is keyed by <c>(<see cref="SensorContext.CurrentRuleId"/>, <see cref="SensorContext.CurrentPath"/>)</c>
/// in <see cref="IConditionTimerStore"/>. The orchestrator seeds <c>CurrentRuleId</c>; recursive
/// evaluators (<see cref="CompositeEvaluator"/>, <see cref="NotEvaluator"/>, this evaluator) maintain
/// <c>CurrentPath</c> as they descend so each sustained instance owns a unique row even when the
/// same condition tree contains multiple sustained nodes. Behaviour:
/// <list type="bullet">
///   <item>Child false: the timer is cleared and this evaluator returns false (the window restarts on the next true).</item>
///   <item>Child true with no existing timer: the current instant is recorded and this evaluator returns false.</item>
///   <item>Child true with an existing timer: returns true once <c>(now - first) &gt;= Minutes</c>.</item>
/// </list>
/// Unknown child types follow the same silent-false fail-mode used by <see cref="CompositeEvaluator"/>
/// and <see cref="NotEvaluator"/>.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="IConditionTimerStore"/>
/// <seealso cref="ConditionEvaluatorRegistry"/>
public class SustainedEvaluator : IConditionEvaluator
{

    private readonly IServiceProvider _serviceProvider;
    private readonly IConditionTimerStore _timerStore;
    private readonly TimeProvider _timeProvider;
    private ConditionEvaluatorRegistry? _registry;

    /// <summary>
    /// Initialises a new <see cref="SustainedEvaluator"/>.
    /// </summary>
    /// <param name="serviceProvider">DI service provider used to lazily resolve <see cref="ConditionEvaluatorRegistry"/> (avoids circular dependencies).</param>
    /// <param name="timerStore">Persistence port for per-(rule, path) first-true timers.</param>
    /// <param name="timeProvider">Abstraction for the current UTC time, enabling deterministic unit tests.</param>
    public SustainedEvaluator(IServiceProvider serviceProvider, IConditionTimerStore timerStore, TimeProvider timeProvider)
    {
        _serviceProvider = serviceProvider;
        _timerStore = timerStore;
        _timeProvider = timeProvider;
    }

    private ConditionEvaluatorRegistry Registry =>
        _registry ??= _serviceProvider.GetRequiredService<ConditionEvaluatorRegistry>();

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.Sustained;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="SustainedCondition"/>.</param>
    /// <param name="context">Current sensor reading context. <see cref="SensorContext.CurrentRuleId"/> and <see cref="SensorContext.CurrentPath"/> key the persistent timer.</param>
    /// <param name="ct">Cancellation token forwarded to the timer store and child evaluator.</param>
    public async Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        var condition = JsonSerializer.Deserialize<SustainedCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition?.Child is null || condition.Minutes <= 0)
            return false;

        var ruleId = context.CurrentRuleId;
        var path = context.CurrentPath;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var childResult = await EvaluateChildAsync(condition.Child, context, ct);

        if (!childResult)
        {
            await _timerStore.ClearAsync(ruleId, path, ct);
            return false;
        }

        var first = await _timerStore.GetFirstTrueAsync(ruleId, path, ct);
        if (first is null)
        {
            await _timerStore.SetFirstTrueAsync(ruleId, path, now, ct);
            return false;
        }

        return (now - first.Value).TotalMinutes >= condition.Minutes;
    }

    private Task<bool> EvaluateChildAsync(ConditionNode node, SensorContext context, CancellationToken ct)
    {
        // Path threading: a sustained wrapper has a single child, indexed as [0] (matches ConditionPath.Walk).
        var childContext = context with { CurrentPath = $"{context.CurrentPath}[0].{node.Type}" };
        return Registry.EvaluateNodeAsync(node, childContext, ct);
    }
}
