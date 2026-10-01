using System.Text.Json;
using System.Text.Json.Nodes;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The condition kinds whose truth can change while every fact except the clock stands still.
/// A rule containing one is evaluated on the wall clock as well as per reading
/// (<c>docs/alerts/engine-semantics.md</c> §5.1). The Rust crate mirrors this list and checks it
/// against the committed enum manifest.
/// </summary>
public static class WallClockConditions
{
    /// <summary>
    /// Kinds that measure elapsed time against an anchor a reading does not move. The anchors are
    /// the newest reading, a loop cycle, a device or tracker start, a treatment, another alert's
    /// trigger or acknowledgement, and the start of a suspension, override, DND or state span.
    /// </summary>
    public static IReadOnlySet<AlertConditionType> Kinds { get; } = new HashSet<AlertConditionType>
    {
        AlertConditionType.SignalLoss,
        AlertConditionType.Staleness,
        AlertConditionType.LoopStale,
        AlertConditionType.LoopEnactionStale,
        AlertConditionType.SiteAge,
        AlertConditionType.SensorAge,
        AlertConditionType.TrackerAge,
        AlertConditionType.TimeSinceLastCarb,
        AlertConditionType.TimeSinceLastBolus,
        AlertConditionType.AlertState,
        AlertConditionType.PumpSuspended,
        AlertConditionType.OverrideActive,
        AlertConditionType.DoNotDisturb,
        AlertConditionType.PumpState,
        AlertConditionType.StateSpanActive,
    };

    /// <summary>
    /// Every other kind, listed so a new <see cref="AlertConditionType"/> member has to be placed
    /// on one side. Containers are here since a <c>sustained</c> timer over a reading-driven
    /// child would only extrapolate the last reading. <c>time_of_day</c> and <c>day_of_week</c>
    /// gate reading-driven leaves, so the next reading bounds their delay.
    /// </summary>
    public static IReadOnlySet<AlertConditionType> NotWallClock { get; } = new HashSet<AlertConditionType>
    {
        AlertConditionType.Threshold,
        AlertConditionType.RateOfChange,
        AlertConditionType.Composite,
        AlertConditionType.Not,
        AlertConditionType.Sustained,
        AlertConditionType.Predicted,
        AlertConditionType.Trend,
        AlertConditionType.TimeOfDay,
        AlertConditionType.Iob,
        AlertConditionType.Cob,
        AlertConditionType.Reservoir,
        AlertConditionType.PumpBattery,
        AlertConditionType.TempBasal,
        AlertConditionType.UploaderBattery,
        AlertConditionType.SensitivityRatio,
        AlertConditionType.GlucoseBucket,
        AlertConditionType.DayOfWeek,
        AlertConditionType.SleepSessionActive,
    };

    /// <summary>
    /// True when the rule's root kind, or any node of its condition tree, is in
    /// <see cref="Kinds"/>. A tree that does not parse is false: node dispatch cannot evaluate it
    /// either.
    /// </summary>
    public static bool ReferencesWallClock(AlertRuleSnapshot rule)
    {
        if (Kinds.Contains(rule.ConditionType)) return true;
        if (rule.ConditionType is not (AlertConditionType.Composite
            or AlertConditionType.Not
            or AlertConditionType.Sustained))
        {
            return false;
        }

        var root = TryBuildNode(rule);
        return root is not null
               && ConditionPath.Walk<object>(root, (node, _) =>
                   AlertConditionTypeNames.Resolve(node.Type) is { } kind && Kinds.Contains(kind)
                       ? node
                       : null) is not null;
    }

    private static ConditionNode? TryBuildNode(AlertRuleSnapshot rule)
    {
        var wire = AlertConditionTypeNames.ToWireString(rule.ConditionType);
        try
        {
            var node = new JsonObject
            {
                ["type"] = wire,
                [wire] = JsonNode.Parse(rule.ConditionParams),
            };
            return node.Deserialize<ConditionNode>(EvaluatorJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
