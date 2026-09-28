using System.Text.Json;
using System.Text.Json.Nodes;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>A condition tree shape whose evaluation throws, and the path of the node it is on.</summary>
/// <param name="Path">Condition path (<see cref="ConditionPath"/> grammar) of the offending node.</param>
/// <param name="Reason">Stable reason code, shared with the Rust engine's <c>Reason::code</c>.</param>
public sealed record ConditionTreeFault(string Path, string Reason);

/// <summary>Thrown before evaluating a tree that <see cref="ConditionTreeFaults"/> rejects.</summary>
public sealed class ConditionTreeFaultException(ConditionTreeFault fault)
    : Exception($"Condition tree cannot be evaluated: {fault.Reason} at '{fault.Path}'")
{
    public ConditionTreeFault Fault { get; } = fault;
}

/// <summary>
/// Finds the tree shapes whose evaluation throws (docs/alerts/engine-semantics.md §1.4) before
/// any node is evaluated. A rule holding one is skipped on every tick with no timer written,
/// whether or not the evaluation order would reach it. The Rust engine rejects the same shapes
/// when it parses a tree, and the golden corpus pins the two together.
/// </summary>
internal static class ConditionTreeFaults
{
    /// <summary>
    /// The first fault in a rule body, rooted at the kind's wire name. A JSON <c>null</c> body is
    /// a null record, which evaluates false; malformed JSON is left for the evaluator to throw on.
    /// </summary>
    public static ConditionTreeFault? InRule(AlertConditionType type, string? conditionParamsJson)
    {
        if (type is not (AlertConditionType.Composite or AlertConditionType.Not
                or AlertConditionType.Sustained or AlertConditionType.Threshold
                or AlertConditionType.RateOfChange or AlertConditionType.AlertState)
            || string.IsNullOrWhiteSpace(conditionParamsJson))
        {
            return null;
        }

        var wire = AlertConditionTypeNames.ToWireString(type);
        ConditionNode? node;
        try
        {
            var payload = JsonNode.Parse(conditionParamsJson);
            if (payload is null)
                return null;
            var envelope = new JsonObject { ["type"] = wire, [wire] = payload };
            node = JsonSerializer.Deserialize<ConditionNode>(envelope.ToJsonString(), EvaluatorJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
        return node is null ? null : InNode(node, wire);
    }

    /// <summary>The first fault, in pre-order, in the tree rooted at <paramref name="node"/>.</summary>
    public static ConditionTreeFault? InNode(ConditionNode? node, string path)
    {
        if (node is null)
            return new(path, "condition_missing");
        if (node.Type is null)
            return new(path, "type_missing");

        var payload = ConditionNodePayloads.Select(node);
        return AlertConditionTypeNames.Resolve(node.Type) switch
        {
            AlertConditionType.Composite => InComposite(payload as CompositeCondition, path),
            AlertConditionType.Not => InChild((payload as NotCondition)?.Child, path),
            AlertConditionType.Sustained => InChild((payload as SustainedCondition)?.Child, path),
            AlertConditionType.Threshold when (payload as ThresholdCondition)?.Direction is null =>
                new(path, "direction_missing"),
            AlertConditionType.RateOfChange when (payload as RateOfChangeCondition)?.Direction is null =>
                new(path, "direction_missing"),
            AlertConditionType.AlertState when (payload as AlertStateCondition)?.State is null =>
                new(path, "state_missing"),
            _ => null,
        };
    }

    private static ConditionTreeFault? InComposite(CompositeCondition? composite, string path)
    {
        if (composite?.Conditions is null)
            return new(path, "conditions_missing");
        if (composite.Conditions.Count == 0)
            return null;
        if (composite.Operator is null)
            return new(path, "operator_missing");

        for (var i = 0; i < composite.Conditions.Count; i++)
        {
            var child = composite.Conditions[i];
            if (InNode(child, $"{path}[{i}].{child?.Type}") is { } fault)
                return fault;
        }
        return null;
    }

    private static ConditionTreeFault? InChild(ConditionNode? child, string path) =>
        child is null ? null : InNode(child, $"{path}[0].{child.Type}");
}
