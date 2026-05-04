using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Resolves an <see cref="AlertConditionType"/> to the corresponding <see cref="IConditionEvaluator"/>,
/// and provides the single node-dispatch entrypoint shared by recursive evaluators
/// (composite/not/sustained), the orchestrator's auto-resolve path, and smart-snooze evaluation.
/// Registered as scoped because evaluators that touch <see cref="IConditionTimerStore"/> are
/// DbContext-backed; the constructor takes all registered evaluators via DI.
/// </summary>
public class ConditionEvaluatorRegistry
{
    private readonly Dictionary<AlertConditionType, IConditionEvaluator> _evaluators;

    /// <summary>
    /// Initialises a new <see cref="ConditionEvaluatorRegistry"/> with all registered evaluators.
    /// </summary>
    /// <param name="evaluators">All <see cref="IConditionEvaluator"/> implementations registered in DI.</param>
    public ConditionEvaluatorRegistry(IEnumerable<IConditionEvaluator> evaluators)
    {
        _evaluators = evaluators.ToDictionary(e => e.ConditionType, e => e);
    }

    /// <summary>
    /// Returns the <see cref="IConditionEvaluator"/> for the specified <paramref name="conditionType"/>,
    /// or <see langword="null"/> if no evaluator is registered for that type.
    /// </summary>
    /// <param name="conditionType">The <see cref="AlertConditionType"/> to look up.</param>
    /// <returns>The matching evaluator, or <see langword="null"/>.</returns>
    public IConditionEvaluator? GetEvaluator(AlertConditionType conditionType)
    {
        _evaluators.TryGetValue(conditionType, out var evaluator);
        return evaluator;
    }

    /// <summary>
    /// Convenience overload for composite sub-condition routing where the type
    /// arrives as a raw JSON string from <see cref="ConditionNode.Type"/>.
    /// </summary>
    public IConditionEvaluator? GetEvaluator(string conditionTypeString)
    {
        var byWire = AlertConditionTypeNames.FromWireString(conditionTypeString);
        if (byWire is not null)
            return GetEvaluator(byWire.Value);

        if (Enum.TryParse<AlertConditionType>(conditionTypeString, ignoreCase: true, out var parsed))
            return GetEvaluator(parsed);

        return null;
    }

    /// <summary>
    /// Single node-dispatch entrypoint: looks up the evaluator for <paramref name="node"/>'s
    /// <see cref="ConditionNode.Type"/>, serialises its kind-specific payload, and delegates.
    /// Used by recursive evaluators (composite/not/sustained), auto-resolve in the orchestrator,
    /// and smart-snooze condition evaluation. Returns <see langword="false"/> when the kind is
    /// unregistered — matches the silent-fail mode the recursive evaluators have always used so
    /// a malformed rule never throws at runtime.
    /// </summary>
    /// <param name="node">The condition node to evaluate. <see cref="ConditionNode.Type"/> selects the evaluator.</param>
    /// <param name="context">Sensor context. Path threading is the caller's responsibility.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> EvaluateNodeAsync(ConditionNode node, SensorContext context, CancellationToken ct)
    {
        var evaluator = GetEvaluator(node.Type);
        if (evaluator is null)
            return false;

        var paramsJson = ConditionNodePayloads.SerializeChildPayload(node, EvaluatorJson.Options);
        return await evaluator.EvaluateAsync(paramsJson, context, ct);
    }
}
