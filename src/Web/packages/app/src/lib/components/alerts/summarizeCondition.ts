import type { ConditionNode, ComparisonOperator, TrendBucket } from "./types";

/**
 * Optional context for summarisation. The <c>resolveAlertName</c> hook lets the
 * caller turn an opaque <c>alert_state</c> rule id into a human label — without
 * it the summariser falls back to a short id suffix.
 */
export interface SummarizeContext {
	resolveAlertName?: (id: string) => string | undefined;
	/** Glucose unit for threshold rendering. Defaults to mg/dL. */
	glucoseUnit?: "mg/dL" | "mmol/L";
}

/**
 * Render a <see cref="ConditionNode"/> as a short, human-readable string for
 * use in row chips on the overview, history descriptions, and the optional
 * auto-derived rule summary on the editor.
 *
 * The output is intentionally compact (no leading "When …", no trailing
 * punctuation) so callers can compose it into surrounding copy.
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
			const joiner = p.operator === "and" ? " AND " : " OR ";
			return p.conditions.map((c) => wrapForJoin(c, p.operator, ctx)).join(joiner);
		}
		case "not": {
			const child = node.not?.child;
			if (!child) return "";
			return `not (${summarizeCondition(child, ctx)})`;
		}
		case "sustained": {
			const p = node.sustained;
			if (!p) return "";
			return `${summarizeCondition(p.child, ctx)} for ${formatMinutes(p.minutes)}`;
		}
		case "threshold": {
			const p = node.threshold;
			if (!p) return "";
			const op = p.direction === "below" ? "<" : ">";
			return `BG ${op} ${p.value} ${ctx.glucoseUnit ?? "mg/dL"}`;
		}
		case "rate_of_change": {
			const p = node.rate_of_change;
			if (!p) return "";
			return `BG ${p.direction} ≥ ${p.rate} mg/dL/min`;
		}
		case "staleness": {
			const p = node.staleness;
			if (!p) return "";
			return `Sensor stale ${opSymbol(p.operator)} ${formatMinutes(p.value)}`;
		}
		case "predicted": {
			const p = node.predicted;
			if (!p) return "";
			return `Predicted BG ${opSymbol(p.operator)} ${p.value} ${ctx.glucoseUnit ?? "mg/dL"} in ${formatMinutes(p.within_minutes)}`;
		}
		case "trend": {
			const p = node.trend;
			if (!p) return "";
			return `Trend: ${trendLabel(p.bucket)}`;
		}
		case "time_of_day": {
			const p = node.time_of_day;
			if (!p) return "";
			return `between ${p.from} and ${p.to}`;
		}
		case "iob": {
			const p = node.iob;
			if (!p) return "";
			return `IOB ${opSymbol(p.operator)} ${p.value} U`;
		}
		case "cob": {
			const p = node.cob;
			if (!p) return "";
			return `COB ${opSymbol(p.operator)} ${p.value} g`;
		}
		case "reservoir": {
			const p = node.reservoir;
			if (!p) return "";
			return `Reservoir ${opSymbol(p.operator)} ${p.value} U`;
		}
		case "site_age": {
			const p = node.site_age;
			if (!p) return "";
			return `Site age ${opSymbol(p.operator)} ${formatHours(p.value)}`;
		}
		case "sensor_age": {
			const p = node.sensor_age;
			if (!p) return "";
			return `Sensor age ${opSymbol(p.operator)} ${formatDays(p.value)}`;
		}
		case "alert_state": {
			const p = node.alert_state;
			if (!p) return "";
			const label = ctx.resolveAlertName?.(p.alert_id) ?? shortId(p.alert_id);
			const sustained = p.for_minutes ? ` for ${formatMinutes(p.for_minutes)}` : "";
			return `${label} ${p.state}${sustained}`;
		}
		case "loop_stale": {
			const p = node.loop_stale;
			if (!p) return "";
			return `Loop stale ${p.operator} ${formatMinutes(p.minutes)}`;
		}
		case "loop_enaction_stale": {
			const p = node.loop_enaction_stale;
			if (!p) return "";
			return `Loop enaction stale ${p.operator} ${formatMinutes(p.minutes)}`;
		}
		case "pump_suspended": {
			const p = node.pump_suspended;
			if (!p) return "";
			const verb = p.is_active ? "Pump suspended" : "Pump not suspended";
			return p.for_minutes ? `${verb} for ${formatMinutes(p.for_minutes)}` : verb;
		}
		case "pump_battery": {
			const p = node.pump_battery;
			if (!p) return "";
			return `Pump battery ${opSymbol(p.operator)} ${p.value}%`;
		}
		case "temp_basal": {
			const p = node.temp_basal;
			if (!p) return "";
			const unit = p.metric === "rate" ? "U/h" : "% of scheduled";
			const label = p.metric === "rate" ? "Temp basal rate" : "Temp basal";
			return `${label} ${opSymbol(p.operator)} ${p.value} ${unit}`;
		}
		case "uploader_battery": {
			const p = node.uploader_battery;
			if (!p) return "";
			return `Uploader battery ${opSymbol(p.operator)} ${p.value}%`;
		}
		case "override_active": {
			const p = node.override_active;
			if (!p) return "";
			const verb = p.is_active ? "Override active" : "No override active";
			return p.for_minutes ? `${verb} for ${formatMinutes(p.for_minutes)}` : verb;
		}
		case "sensitivity_ratio": {
			const p = node.sensitivity_ratio;
			if (!p) return "";
			return `Sensitivity ${opSymbol(p.operator)} ${p.value}`;
		}
		case "do_not_disturb": {
			const p = node.do_not_disturb;
			if (!p) return "";
			const verb = p.is_active ? "Do Not Disturb on" : "Do Not Disturb off";
			return p.for_minutes ? `${verb} for ${formatMinutes(p.for_minutes)}` : verb;
		}
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

function opSymbol(op: ComparisonOperator | ">" | ">="): string {
	switch (op) {
		case "<":
			return "<";
		case "<=":
			return "≤";
		case ">":
			return ">";
		case ">=":
			return "≥";
	}
}

function trendLabel(bucket: TrendBucket): string {
	switch (bucket) {
		case "falling_fast":
			return "falling fast";
		case "falling":
			return "falling";
		case "flat":
			return "flat";
		case "rising":
			return "rising";
		case "rising_fast":
			return "rising fast";
	}
}

function formatMinutes(minutes: number): string {
	if (minutes >= 60 && minutes % 60 === 0) {
		const hours = minutes / 60;
		return `${hours}h`;
	}
	return `${minutes}m`;
}

function formatHours(hours: number): string {
	if (hours >= 24 && hours % 24 === 0) {
		const days = hours / 24;
		return `${days}d`;
	}
	return `${hours}h`;
}

function formatDays(days: number): string {
	return `${days}d`;
}

function shortId(id: string): string {
	if (!id) return "alert";
	const tail = id.replace(/-/g, "").slice(-6);
	return `alert ${tail}`;
}
