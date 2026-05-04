using System.Text.Json;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Serialises the type-discriminated payload of a <see cref="ConditionNode"/> back to JSON
/// so a parent recursive evaluator (composite, not, sustained) can hand it to the matching
/// leaf <see cref="Nocturne.Core.Contracts.Alerts.IConditionEvaluator"/>.
/// </summary>
/// <remarks>
/// Exists to consolidate the type switch that would otherwise be duplicated across every
/// recursive evaluator. When a new <see cref="Nocturne.Core.Models.Alerts.AlertConditionType"/>
/// is added, this is the only switch that needs updating.
/// </remarks>
internal static class ConditionNodePayloads
{
    /// <summary>
    /// Returns the JSON representation of the populated payload field on <paramref name="node"/>,
    /// chosen by <see cref="ConditionNode.Type"/>. Returns <c>"{}"</c> when the type is unknown
    /// or the matching payload field is null — preserves the silent fail-mode used by recursive
    /// evaluators so a malformed rule never throws at runtime.
    /// </summary>
    /// <param name="node">The node whose payload to serialise.</param>
    /// <param name="options">JSON options used by the caller (snake_case naming, case-insensitive read).</param>
    public static string SerializeChildPayload(ConditionNode node, JsonSerializerOptions options)
    {
        var payload = node.Type.ToLowerInvariant() switch
        {
            "threshold" => (object?)node.Threshold,
            "rate_of_change" => node.RateOfChange,
            "signal_loss" => node.SignalLoss,
            "composite" => node.Composite,
            "not" => node.Not,
            "sustained" => node.Sustained,
            "staleness" => node.Staleness,
            "predicted" => node.Predicted,
            "trend" => node.Trend,
            "time_of_day" => node.TimeOfDay,
            "iob" => node.Iob,
            "cob" => node.Cob,
            "reservoir" => node.Reservoir,
            "site_age" => node.SiteAge,
            "sensor_age" => node.SensorAge,
            "alert_state" => node.AlertState,
            "loop_stale" => node.LoopStale,
            "loop_enaction_stale" => node.LoopEnactionStale,
            "pump_suspended" => node.PumpSuspended,
            "pump_battery" => node.PumpBattery,
            "temp_basal" => node.TempBasal,
            "uploader_battery" => node.UploaderBattery,
            "override_active" => node.OverrideActive,
            "sensitivity_ratio" => node.SensitivityRatio,
            "do_not_disturb" => node.DoNotDisturb,
            "glucose_bucket" => node.GlucoseBucket,
            "time_since_last_carb" => node.TimeSinceLastCarb,
            "time_since_last_bolus" => node.TimeSinceLastBolus,
            "day_of_week" => node.DayOfWeek,
            "pump_state" => node.PumpState,
            "state_span_active" => node.StateSpanActive,
            _ => null,
        };

        // Use payload.GetType() rather than <object?> so System.Text.Json serialises the
        // concrete record's properties — passing object would serialise an empty {}.
        return payload is null ? "{}" : JsonSerializer.Serialize(payload, payload.GetType(), options);
    }
}
