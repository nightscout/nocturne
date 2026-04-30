using System.Text.Json;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The set of optional <see cref="SensorContext"/> fields any rule in a batch references,
/// computed by walking each rule's condition tree once before evaluation begins.
/// </summary>
/// <remarks>
/// Used by <see cref="ISensorContextEnricher"/> implementations to skip fetches whose result
/// no rule will read this pass — e.g. a tenant whose only enabled rule is a glucose threshold
/// avoids loading treatments, predictions, device events, and active alert snapshots.
/// </remarks>
public sealed record DataNeedsSet(
    bool NeedsIob,
    bool NeedsCob,
    bool NeedsPredicted,
    bool NeedsReservoir,
    bool NeedsSiteAge,
    bool NeedsSensorAge,
    bool NeedsTrendBucket,
    bool NeedsActiveAlerts)
{
    /// <summary>An empty needs set with all flags false.</summary>
    public static DataNeedsSet None { get; } = new(false, false, false, false, false, false, false, false);
}

/// <summary>
/// Walks a batch of <see cref="AlertRuleSnapshot"/> records and reports which optional
/// <see cref="SensorContext"/> fields need to be populated to evaluate them.
/// </summary>
/// <remarks>
/// Each rule's <see cref="AlertRuleSnapshot.ConditionType"/> drives the top-level entry; for
/// the recursive wrappers (<c>composite</c>, <c>not</c>, <c>sustained</c>) the JSON payload is
/// deserialised once per rule and the resulting <see cref="ConditionNode"/> tree is walked via
/// <see cref="ConditionPath.Walk"/>. Malformed JSON is treated as "no needs" — rule evaluation
/// will fail later with the same silent fail-mode used by the leaf evaluators, so the enricher
/// must not re-throw here.
/// </remarks>
public static class RuleDataNeeds
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Walks <paramref name="rules"/> and returns a <see cref="DataNeedsSet"/> with a flag
    /// set for every kind of optional context any rule depends on.
    /// </summary>
    public static DataNeedsSet Walk(IEnumerable<AlertRuleSnapshot> rules)
    {
        var iob = false;
        var cob = false;
        var predicted = false;
        var reservoir = false;
        var siteAge = false;
        var sensorAge = false;
        var trend = false;
        var activeAlerts = false;

        foreach (var rule in rules)
        {
            VisitTopLevel(rule, ref iob, ref cob, ref predicted, ref reservoir,
                ref siteAge, ref sensorAge, ref trend, ref activeAlerts);
        }

        return new DataNeedsSet(iob, cob, predicted, reservoir, siteAge, sensorAge, trend, activeAlerts);
    }

    private static void VisitTopLevel(
        AlertRuleSnapshot rule,
        ref bool iob, ref bool cob, ref bool predicted, ref bool reservoir,
        ref bool siteAge, ref bool sensorAge, ref bool trend, ref bool activeAlerts)
    {
        switch (rule.ConditionType)
        {
            case AlertConditionType.Composite:
                {
                    var composite = TryDeserialize<CompositeCondition>(rule.ConditionParams);
                    if (composite is null) return;
                    foreach (var child in composite.Conditions)
                    {
                        VisitNode(child, ref iob, ref cob, ref predicted, ref reservoir,
                            ref siteAge, ref sensorAge, ref trend, ref activeAlerts);
                    }
                    return;
                }
            case AlertConditionType.Not:
                {
                    var not = TryDeserialize<NotCondition>(rule.ConditionParams);
                    if (not is null) return;
                    VisitNode(not.Child, ref iob, ref cob, ref predicted, ref reservoir,
                        ref siteAge, ref sensorAge, ref trend, ref activeAlerts);
                    return;
                }
            case AlertConditionType.Sustained:
                {
                    var sustained = TryDeserialize<SustainedCondition>(rule.ConditionParams);
                    if (sustained is null) return;
                    VisitNode(sustained.Child, ref iob, ref cob, ref predicted, ref reservoir,
                        ref siteAge, ref sensorAge, ref trend, ref activeAlerts);
                    return;
                }
            default:
                ApplyLeaf(rule.ConditionType, ref iob, ref cob, ref predicted, ref reservoir,
                    ref siteAge, ref sensorAge, ref trend, ref activeAlerts);
                return;
        }
    }

    private static void VisitNode(
        ConditionNode node,
        ref bool iob, ref bool cob, ref bool predicted, ref bool reservoir,
        ref bool siteAge, ref bool sensorAge, ref bool trend, ref bool activeAlerts)
    {
        // ConditionPath.Walk recurses through composite/not/sustained wrappers and visits every
        // node — exactly what we want; we only ever need the node's Type to update flags.
        // Captures all eight flags by ref via the closure parameters below, hence the local
        // shadow assignments after Walk returns.
        var localIob = iob;
        var localCob = cob;
        var localPredicted = predicted;
        var localReservoir = reservoir;
        var localSiteAge = siteAge;
        var localSensorAge = sensorAge;
        var localTrend = trend;
        var localActiveAlerts = activeAlerts;

        ConditionPath.Walk<object>(node, (visited, _) =>
        {
            var kind = AlertConditionTypeNames.FromWireString(visited.Type);
            if (kind is not null)
            {
                ApplyLeaf(kind.Value, ref localIob, ref localCob, ref localPredicted, ref localReservoir,
                    ref localSiteAge, ref localSensorAge, ref localTrend, ref localActiveAlerts);
            }
            return null;
        });

        iob = localIob;
        cob = localCob;
        predicted = localPredicted;
        reservoir = localReservoir;
        siteAge = localSiteAge;
        sensorAge = localSensorAge;
        trend = localTrend;
        activeAlerts = localActiveAlerts;
    }

    private static void ApplyLeaf(
        AlertConditionType type,
        ref bool iob, ref bool cob, ref bool predicted, ref bool reservoir,
        ref bool siteAge, ref bool sensorAge, ref bool trend, ref bool activeAlerts)
    {
        switch (type)
        {
            case AlertConditionType.Iob: iob = true; break;
            case AlertConditionType.Cob: cob = true; break;
            case AlertConditionType.Predicted: predicted = true; break;
            case AlertConditionType.Reservoir: reservoir = true; break;
            case AlertConditionType.SiteAge: siteAge = true; break;
            case AlertConditionType.SensorAge: sensorAge = true; break;
            case AlertConditionType.Trend: trend = true; break;
            case AlertConditionType.AlertState: activeAlerts = true; break;
            // Threshold, RateOfChange, SignalLoss, Staleness, TimeOfDay, Composite, Not, Sustained
            // require no extra context — handled by base SensorContext or recursed by VisitNode.
        }
    }

    private static T? TryDeserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
