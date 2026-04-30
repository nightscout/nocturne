using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a logical-NOT wrapper, inverting the result of its single child condition.
/// </summary>
/// <remarks>
/// Child routing is delegated to the <see cref="ConditionEvaluatorRegistry"/> resolved
/// lazily from the DI container to avoid circular dependencies. If the child node is
/// missing or its type is not registered, the child evaluation is treated as
/// <see langword="false"/> and this evaluator therefore returns <see langword="true"/> —
/// the same silent fail-mode used by <see cref="CompositeEvaluator"/> for unknown sub-types.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="ConditionEvaluatorRegistry"/>
public class NotEvaluator : IConditionEvaluator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly IServiceProvider _serviceProvider;
    private ConditionEvaluatorRegistry? _registry;

    /// <summary>
    /// Initialises a new <see cref="NotEvaluator"/>.
    /// </summary>
    /// <param name="serviceProvider">
    /// The DI service provider used for lazy resolution of <see cref="ConditionEvaluatorRegistry"/>.
    /// </param>
    public NotEvaluator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    private ConditionEvaluatorRegistry Registry =>
        _registry ??= _serviceProvider.GetRequiredService<ConditionEvaluatorRegistry>();

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.Not;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="NotCondition"/>.</param>
    /// <param name="context">Current sensor reading context forwarded to the child evaluator.</param>
    public bool Evaluate(string conditionParamsJson, SensorContext context)
    {
        var condition = JsonSerializer.Deserialize<NotCondition>(conditionParamsJson, JsonOptions);
        if (condition?.Child is null)
            return false;

        return !EvaluateChild(condition.Child, context);
    }

    private bool EvaluateChild(ConditionNode node, SensorContext context)
    {
        var evaluator = Registry.GetEvaluator(node.Type);
        if (evaluator is null)
            return false;

        var paramsJson = node.Type.ToLowerInvariant() switch
        {
            "threshold" => JsonSerializer.Serialize(node.Threshold, JsonOptions),
            "rate_of_change" => JsonSerializer.Serialize(node.RateOfChange, JsonOptions),
            "signal_loss" => JsonSerializer.Serialize(node.SignalLoss, JsonOptions),
            "composite" => JsonSerializer.Serialize(node.Composite, JsonOptions),
            "not" => JsonSerializer.Serialize(node.Not, JsonOptions),
            "staleness" => JsonSerializer.Serialize(node.Staleness, JsonOptions),
            _ => "{}"
        };

        return evaluator.Evaluate(paramsJson, context);
    }
}
