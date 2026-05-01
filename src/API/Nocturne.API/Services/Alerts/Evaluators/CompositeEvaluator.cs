using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a composite alert condition whose sub-conditions are combined with logical
/// <c>AND</c> or <c>OR</c> operators.
/// </summary>
/// <remarks>
/// Sub-condition routing is delegated to the <see cref="ConditionEvaluatorRegistry"/> resolved
/// lazily from the DI container to avoid circular dependencies. Each sub-condition is
/// re-serialised from the <see cref="ConditionNode"/> and forwarded to the appropriate
/// <see cref="IConditionEvaluator"/> implementation.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="ConditionEvaluatorRegistry"/>
public class CompositeEvaluator : IConditionEvaluator
{

    private readonly IServiceProvider _serviceProvider;
    private ConditionEvaluatorRegistry? _registry;

    /// <summary>
    /// Initialises a new <see cref="CompositeEvaluator"/>.
    /// </summary>
    /// <param name="serviceProvider">
    /// The DI service provider used for lazy resolution of <see cref="ConditionEvaluatorRegistry"/>.
    /// </param>
    public CompositeEvaluator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    private ConditionEvaluatorRegistry Registry =>
        _registry ??= _serviceProvider.GetRequiredService<ConditionEvaluatorRegistry>();

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.Composite;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="CompositeCondition"/>.</param>
    /// <param name="context">Current sensor reading context passed to each sub-evaluator.</param>
    /// <param name="ct">Cancellation token forwarded to each sub-evaluator.</param>
    /// <returns>
    /// <see langword="true"/> when all (AND) or any (OR) sub-conditions evaluate to <see langword="true"/>;
    /// <see langword="false"/> if the condition is null, empty, or has an unrecognised operator.
    /// </returns>
    public async Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        var condition = JsonSerializer.Deserialize<CompositeCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null || condition.Conditions.Count == 0)
            return false;

        var op = condition.Operator.ToLowerInvariant();

        // Manual short-circuit foreach: LINQ All/Any don't compose with async predicates without
        // additional libraries, and we want to await each child before deciding to recurse.
        if (op == "and")
        {
            for (var i = 0; i < condition.Conditions.Count; i++)
            {
                if (!await EvaluateNodeAsync(condition.Conditions[i], i, context, ct))
                    return false;
            }
            return true;
        }

        if (op == "or")
        {
            for (var i = 0; i < condition.Conditions.Count; i++)
            {
                if (await EvaluateNodeAsync(condition.Conditions[i], i, context, ct))
                    return true;
            }
            return false;
        }

        return false;
    }

    private Task<bool> EvaluateNodeAsync(ConditionNode node, int index, SensorContext context, CancellationToken ct)
    {
        // Path threading: descend into child[index] of kind <node.Type> (matches ConditionPath.Walk).
        var childContext = context with { CurrentPath = $"{context.CurrentPath}[{index}].{node.Type}" };
        return Registry.EvaluateNodeAsync(node, childContext, ct);
    }
}
