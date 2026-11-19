import {
	AlertConditionType,
	AlertPresetGroup,
	AlertRuleSeverity,
	TrackerCategory,
	type AlertPresetResponse,
} from "#lib/api/generated/nocturne-api-client.js";

/** What `GET /api/v4/alert-rules/presets` answers, for a test that renders the alert wizard. */
export const ALERT_PRESETS: AlertPresetResponse[] = [
	preset("urgent_low", AlertPresetGroup.Common, AlertRuleSeverity.Critical, true, true, AlertConditionType.Threshold, {
		direction: "below",
		value: 55,
	}),
	preset("low", AlertPresetGroup.Common, AlertRuleSeverity.Warning, true, true, AlertConditionType.Threshold, {
		direction: "below",
		value: 70,
	}),
	preset("high", AlertPresetGroup.Common, AlertRuleSeverity.Warning, true, true, AlertConditionType.Threshold, {
		direction: "above",
		value: 250,
	}),
	preset("falling_fast", AlertPresetGroup.Common, AlertRuleSeverity.Warning, true, false, AlertConditionType.RateOfChange, {
		direction: "falling",
		rate: 3,
	}),
	preset("rising_fast", AlertPresetGroup.Common, AlertRuleSeverity.Info, true, false, AlertConditionType.RateOfChange, {
		direction: "rising",
		rate: 3,
	}),
	preset("no_readings", AlertPresetGroup.Common, AlertRuleSeverity.Warning, false, false, AlertConditionType.SignalLoss, {
		timeout_minutes: 20,
	}),
	preset("urgent_low_soon", AlertPresetGroup.More, AlertRuleSeverity.Critical, false, false, AlertConditionType.Predicted, {
		operator: "<=",
		value: 55,
		within_minutes: 20,
	}),
	preset("reservoir_low", AlertPresetGroup.More, AlertRuleSeverity.Warning, false, true, AlertConditionType.Reservoir, {
		operator: "<=",
		value: 20,
	}),
	preset("pump_battery_low", AlertPresetGroup.More, AlertRuleSeverity.Warning, false, true, AlertConditionType.PumpBattery, {
		operator: "<=",
		value: 20,
	}),
	{
		...preset("battery_change_due", AlertPresetGroup.More, AlertRuleSeverity.Info, false, true),
		trackerCategories: [TrackerCategory.Battery],
		trackerOnlyAgeHours: 504,
	},
	{
		...preset("site_change_due", AlertPresetGroup.More, AlertRuleSeverity.Info, false, true, AlertConditionType.SiteAge, {
			operator: ">=",
			value: 72,
		}),
		trackerCategories: [TrackerCategory.Cannula, TrackerCategory.Pod],
	},
	{
		...preset("sensor_change_due", AlertPresetGroup.More, AlertRuleSeverity.Info, false, true, AlertConditionType.SensorAge, {
			operator: ">=",
			value: 10,
		}),
		trackerCategories: [TrackerCategory.Sensor],
	},
	preset("loop_not_running", AlertPresetGroup.More, AlertRuleSeverity.Warning, false, false, AlertConditionType.LoopStale, {
		operator: ">",
		minutes: 30,
	}),
	preset("phone_battery_low", AlertPresetGroup.More, AlertRuleSeverity.Info, false, true, AlertConditionType.UploaderBattery, {
		operator: "<=",
		value: 20,
	}),
];

function preset(
	id: string,
	group: AlertPresetGroup,
	severity: AlertRuleSeverity,
	canWait: boolean,
	autoResolve: boolean,
	conditionType?: AlertConditionType,
	conditionParams?: Record<string, unknown>,
): AlertPresetResponse {
	return { id, group, severity, canWait, autoResolve, conditionType, conditionParams, trackerCategories: [] };
}
