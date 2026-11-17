import type { ConditionNode } from "./types";
import { AlertWeekday, PumpModeState, StateSpanCategory } from "#lib/api/generated/nocturne-api-client.js";
import { bg, bgLabel, formatLocale, weekdayLabel } from "#lib/utils/formatting.js";
import { plural } from "#lib/utils/plural.js";
import { glucoseBucketLabel } from "./alert-labels.svelte";

/**
 * Optional context for summarisation. The <c>resolveAlertName</c> hook lets the
 * caller turn an opaque <c>alert_state</c> rule id into a human label. Without
 * it the summariser falls back to a short id suffix.
 */
export interface SummarizeContext {
	resolveAlertName?: (id: string) => string | undefined;
}

/**
 * Render a <see cref="ConditionNode"/> as a short, human-readable string for
 * use in row chips on the overview, history descriptions, and the optional
 * auto-derived rule summary on the editor.
 *
 * Every chip is a whole phrase built per call so wuchale reads it in the
 * current locale. Leaves with an operator field render it as a symbol.
 */
export function summarizeCondition(
	node: ConditionNode | null | undefined,
	ctx: SummarizeContext = {},
): string {
	if (!node) return "";
	switch (node.type) {
		case "composite": {
			const p = node.composite;
			if (!p || p.conditions.length === 0) return "";
			if (p.conditions.length === 1) return summarizeCondition(p.conditions[0], ctx);
			const parts = p.conditions.map((c) => wrapForJoin(c, p.operator, ctx));
			return parts.reduce((joined, part) =>
				p.operator === "and" ? `${joined} and ${part}` : `${joined} or ${part}`,
			);
		}
		case "not": {
			const child = node.not?.child;
			if (!child) return "";
			return `Not (${summarizeCondition(child, ctx)})`;
		}
		case "sustained": {
			const p = node.sustained;
			if (!p) return "";
			return withDuration(summarizeCondition(p.child, ctx), p.minutes);
		}
		case "threshold": {
			const p = node.threshold;
			if (!p) return "";
			return p.direction === "below"
				? `Glucose below ${bg(p.value ?? 0)} ${bgLabel()}`
				: `Glucose above ${bg(p.value ?? 0)} ${bgLabel()}`;
		}
		case "rate_of_change": {
			const p = node.rate_of_change;
			if (!p) return "";
			const rate = `${bg(p.rate ?? 0)} ${bgLabel()} per minute`;
			switch (p.direction) {
				case "falling":
					return `Glucose falling at least ${rate}`;
				case "rising":
					return `Glucose rising at least ${rate}`;
				default:
					return `Glucose changing at least ${rate}`;
			}
		}
		case "staleness": {
			const p = node.staleness;
			if (!p) return "";
			return `Sensor data age ${opSymbol(p.operator)} ${formatMinutes(p.value ?? 0)}`;
		}
		case "predicted": {
			const p = node.predicted;
			if (!p) return "";
			return `Predicted glucose ${opSymbol(p.operator)} ${bg(p.value ?? 0)} ${bgLabel()} in ${formatMinutes(p.within_minutes ?? 0)}`;
		}
		case "trend": {
			const p = node.trend;
			if (!p) return "";
			return trendChip(p.bucket);
		}
		case "time_of_day": {
			const p = node.time_of_day;
			if (!p) return "";
			return `Between ${p.from} and ${p.to}`;
		}
		case "iob": {
			const p = node.iob;
			if (!p) return "";
			return `Insulin on board ${opSymbol(p.operator)} ${p.value} U`;
		}
		case "cob": {
			const p = node.cob;
			if (!p) return "";
			return `Carbs on board ${opSymbol(p.operator)} ${p.value} g`;
		}
		case "reservoir": {
			const p = node.reservoir;
			if (!p) return "";
			return `Reservoir ${opSymbol(p.operator)} ${p.value} U`;
		}
		case "site_age": {
			const p = node.site_age;
			if (!p) return "";
			return `Site age ${opSymbol(p.operator)} ${formatHours(p.value ?? 0)}`;
		}
		case "sensor_age": {
			const p = node.sensor_age;
			if (!p) return "";
			return `Sensor age ${opSymbol(p.operator)} ${formatDays(p.value ?? 0)}`;
		}
		case "alert_state": {
			const p = node.alert_state;
			if (!p) return "";
			const alertId = p.alert_id ?? "";
			const name = ctx.resolveAlertName?.(alertId) ?? shortId(alertId);
			return withDuration(alertStateChip(name, p.state), p.for_minutes);
		}
		case "loop_stale": {
			const p = node.loop_stale;
			if (!p) return "";
			return `Loop stale ${opSymbol(p.operator)} ${formatMinutes(p.minutes ?? 0)}`;
		}
		case "loop_enaction_stale": {
			const p = node.loop_enaction_stale;
			if (!p) return "";
			return `Loop enaction stale ${opSymbol(p.operator)} ${formatMinutes(p.minutes ?? 0)}`;
		}
		case "pump_suspended": {
			const p = node.pump_suspended;
			if (!p) return "";
			return withDuration(p.is_active ? "Pump suspended" : "Pump not suspended", p.for_minutes);
		}
		case "pump_battery": {
			const p = node.pump_battery;
			if (!p) return "";
			return `Pump battery ${opSymbol(p.operator)} ${p.value}%`;
		}
		case "temp_basal": {
			const p = node.temp_basal;
			if (!p) return "";
			return p.metric === "rate"
				? `Temp basal rate ${opSymbol(p.operator)} ${p.value} U/h`
				: `Temp basal ${opSymbol(p.operator)} ${p.value}% of scheduled`;
		}
		case "uploader_battery": {
			const p = node.uploader_battery;
			if (!p) return "";
			return `Uploader battery ${opSymbol(p.operator)} ${p.value}%`;
		}
		case "override_active": {
			const p = node.override_active;
			if (!p) return "";
			return withDuration(p.is_active ? "Override active" : "No override active", p.for_minutes);
		}
		case "sensitivity_ratio": {
			const p = node.sensitivity_ratio;
			if (!p) return "";
			return `Sensitivity ${opSymbol(p.operator)} ${p.value}`;
		}
		case "do_not_disturb": {
			const p = node.do_not_disturb;
			if (!p) return "";
			return withDuration(p.is_active ? "Do not disturb on" : "Do not disturb off", p.for_minutes);
		}
		case "signal_loss": {
			const p = node.signal_loss;
			if (!p) return "";
			return `No data for at least ${formatMinutes(p.timeout_minutes ?? 0)}`;
		}
		case "glucose_bucket": {
			const p = node.glucose_bucket;
			if (!p || !p.buckets || p.buckets.length === 0) return "";
			return `Glucose range: ${listFormat().format(p.buckets.map(glucoseBucketLabel))}`;
		}
		case "time_since_last_carb": {
			const p = node.time_since_last_carb;
			if (!p) return "";
			return `Time since last carb ${opSymbol(p.operator)} ${formatMinutes(p.minutes ?? 0)}`;
		}
		case "time_since_last_bolus": {
			const p = node.time_since_last_bolus;
			if (!p) return "";
			return `Time since last bolus ${opSymbol(p.operator)} ${formatMinutes(p.minutes ?? 0)}`;
		}
		case "day_of_week": {
			const p = node.day_of_week;
			if (!p || !p.days || p.days.length === 0) return "";
			return `On ${listFormat().format(p.days.map(alertWeekdayLabel))}`;
		}
		case "pump_state": {
			const p = node.pump_state;
			if (!p) return "";
			return withDuration(pumpStateChip(p.mode, p.is_active), p.for_minutes);
		}
		case "state_span_active": {
			const p = node.state_span_active;
			if (!p) return "";
			const chip = stateSpanChip(p.category, p.is_active);
			return withDuration(p.state ? `${chip} (${p.state})` : chip, p.for_minutes);
		}
		case "sleep_session_active": {
			const p = node.sleep_session_active;
			if (!p) return "";
			return p.is_active ? "Sleep session active" : "No sleep session active";
		}
		case "tracker_age": {
			const p = node.tracker_age;
			if (!p) return "";
			return `Tracker age ${opSymbol(p.operator)} ${formatSignedMinutes(p.minutes ?? 0)}`;
		}
	}
}

function withDuration(chip: string, minutes: number | undefined): string {
	return minutes ? `${chip} for ${formatMinutes(minutes)}` : chip;
}

function listFormat(): Intl.ListFormat {
	return new Intl.ListFormat(formatLocale(), { style: "long", type: "disjunction" });
}

export function alertWeekdayLabel(day: AlertWeekday): string {
	const index = Object.values(AlertWeekday).indexOf(day);
	return index < 0 ? day : weekdayLabel(index);
}

function pumpStateChip(mode: PumpModeState | undefined, isActive: boolean | undefined): string {
	switch (mode) {
		case PumpModeState.Automatic:
			return isActive ? "Pump in automatic mode" : "Pump not in automatic mode";
		case PumpModeState.Limited:
			return isActive ? "Pump limited" : "Pump not limited";
		case PumpModeState.Manual:
			return isActive ? "Pump in manual mode" : "Pump not in manual mode";
		case PumpModeState.Boost:
			return isActive ? "Pump boosting" : "Pump not boosting";
		case PumpModeState.EaseOff:
			return isActive ? "Pump easing off" : "Pump not easing off";
		case PumpModeState.Sleep:
			return isActive ? "Pump in sleep mode" : "Pump not in sleep mode";
		case PumpModeState.Exercise:
			return isActive ? "Pump in exercise mode" : "Pump not in exercise mode";
		case PumpModeState.Liberty:
			return isActive ? "Pump in Liberty mode" : "Pump not in Liberty mode";
		case PumpModeState.Suspended:
			return isActive ? "Pump suspended" : "Pump not suspended";
		case PumpModeState.Off:
			return isActive ? "Pump off" : "Pump not off";
		default:
			return isActive ? "Pump mode active" : "Pump mode not active";
	}
}

function stateSpanChip(category: StateSpanCategory | undefined, isActive: boolean | undefined): string {
	switch (category) {
		case StateSpanCategory.PumpMode:
			return isActive ? "Pump mode active" : "Pump mode not active";
		case StateSpanCategory.PumpConnectivity:
			return isActive ? "Pump connectivity active" : "Pump connectivity not active";
		case StateSpanCategory.Override:
			return isActive ? "Override active" : "Override not active";
		case StateSpanCategory.Profile:
			return isActive ? "Profile active" : "Profile not active";
		case StateSpanCategory.Exercise:
			return isActive ? "Exercise active" : "Exercise not active";
		case StateSpanCategory.Illness:
			return isActive ? "Illness active" : "Illness not active";
		case StateSpanCategory.Travel:
			return isActive ? "Travel active" : "Travel not active";
		case StateSpanCategory.DataExclusion:
			return isActive ? "Data exclusion active" : "Data exclusion not active";
		case StateSpanCategory.TemporaryTarget:
			return isActive ? "Temporary target active" : "Temporary target not active";
		case StateSpanCategory.Motion:
			return isActive ? "Motion active" : "Motion not active";
		default:
			return isActive ? "State active" : "State not active";
	}
}

function alertStateChip(name: string, state: string | undefined): string {
	switch (state) {
		case "firing":
			return `Alert ${name} firing`;
		case "unacknowledged":
			return `Alert ${name} unacknowledged`;
		case "acknowledged":
			return `Alert ${name} acknowledged`;
		default:
			return `Alert ${name} in state ${state ?? ""}`;
	}
}

function trendChip(bucket: string | undefined): string {
	switch (bucket) {
		case "falling_fast":
			return "Trend falling fast";
		case "falling":
			return "Trend falling";
		case "flat":
			return "Trend flat";
		case "rising":
			return "Trend rising";
		case "rising_fast":
			return "Trend rising fast";
		default:
			return `Trend ${bucket ?? ""}`;
	}
}

/**
 * When a child of a composite is itself a composite with a *different*
 * operator, we need to bracket it to preserve precedence in the rendered
 * string (e.g. `(time AND sustained) OR predicted`).
 */
function wrapForJoin(
	child: ConditionNode,
	parentOperator: "and" | "or",
	ctx: SummarizeContext,
): string {
	const summary = summarizeCondition(child, ctx);
	if (
		child.type === "composite" &&
		child.composite &&
		child.composite.operator !== parentOperator &&
		child.composite.conditions.length > 1
	) {
		return `(${summary})`;
	}
	return summary;
}

function opSymbol(op: string | undefined): string {
	switch (op) {
		case "<":
			return "<";
		case "<=":
			return "≤";
		case ">":
			return ">";
		case ">=":
			return "≥";
		default:
			return op ?? "";
	}
}

function formatMinutes(minutes: number): string {
	if (minutes >= 60 && minutes % 60 === 0) return plural(minutes / 60, ["# hour", "# hours"]);
	return plural(minutes, ["# minute", "# minutes"]);
}

/** Like {@link formatMinutes}, but keeps the sign for negative durations
 *  (tracker_age uses negative minutes for "before the scheduled event"). */
function formatSignedMinutes(minutes: number): string {
	return minutes < 0 ? `-${formatMinutes(-minutes)}` : formatMinutes(minutes);
}

function formatHours(hours: number): string {
	if (hours >= 24 && hours % 24 === 0) return formatDays(hours / 24);
	return plural(hours, ["# hour", "# hours"]);
}

function formatDays(days: number): string {
	return plural(days, ["# day", "# days"]);
}

function shortId(id: string): string {
	if (!id) return "?";
	return id.replace(/-/g, "").slice(-6);
}
