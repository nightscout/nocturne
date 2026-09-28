import {
	AlertConditionType,
	AlertReplayEventKind,
	AlertRuleSeverity,
	type AlertReplayResult,
	type AlertRuleResponse,
	type DashboardChartData,
	type FactSnapshotPoint,
} from "@nocturne/app/api-client";
import { transformChartData, type TransformedChartData } from "@nocturne/app/utils/chart-data-transform";
import { wirePayload, type ConditionNode, type ReplayResult, type ReplayTick, type Severity, type SimRule } from "./wire";
import { carbsOnBoard, insulinOnBoard, tickTimes, type Scenario } from "./fixtures";

/** The engine's own event kinds, less `cleared`, which the app's replay does not report. */
const EVENT_KINDS: Partial<Record<ReplayResult["events"][number]["kind"], AlertReplayEventKind>> = {
	fired: AlertReplayEventKind.Fired,
	suppressed_by_dnd: AlertReplayEventKind.SuppressedByDnd,
	auto_resolved: AlertReplayEventKind.AutoResolved,
};

const CONDITION_TYPES: Record<ConditionNode["type"], AlertConditionType> = {
	composite: AlertConditionType.Composite,
	not: AlertConditionType.Not,
	sustained: AlertConditionType.Sustained,
	threshold: AlertConditionType.Threshold,
	rate_of_change: AlertConditionType.RateOfChange,
	signal_loss: AlertConditionType.SignalLoss,
	time_of_day: AlertConditionType.TimeOfDay,
	cob: AlertConditionType.Cob,
	iob: AlertConditionType.Iob,
	time_since_last_carb: AlertConditionType.TimeSinceLastCarb,
};

const SEVERITIES: Record<Severity, AlertRuleSeverity> = {
	critical: AlertRuleSeverity.Critical,
	warning: AlertRuleSeverity.Warning,
	info: AlertRuleSeverity.Info,
};

/** Each rule as the app's API returns one: the root node's payload is `conditionParams`. */
export function ruleResponses(rules: SimRule[]): AlertRuleResponse[] {
	return rules.map((rule, index) => ({
		id: rule.id,
		name: rule.name,
		severity: SEVERITIES[rule.severity],
		conditionType: CONDITION_TYPES[rule.condition.type],
		conditionParams: wirePayload(rule.condition),
		isEnabled: true,
		sortOrder: index,
		autoResolveEnabled: rule.autoResolve !== undefined,
		autoResolveParams: rule.autoResolve,
	}));
}

/** The fact keys the app's rule sidebar reads a leaf's current value from (factSnapshot.ts). */
function factTimelines(ticks: ReplayTick[]): Record<string, FactSnapshotPoint[]> {
	const timelines: Record<string, FactSnapshotPoint[]> = {
		latest_glucose: [],
		trend_rate: [],
		iob: [],
		cob: [],
		time_since_last_carb_minutes: [],
	};
	for (const { at, context } of ticks) {
		const atMs = Date.parse(at);
		const push = (key: string, value: number | undefined) => {
			if (value !== undefined) timelines[key].push({ atMs, value });
		};
		push("latest_glucose", context.latest_value);
		push("trend_rate", context.trend_rate);
		push("iob", context.iob_units);
		push("cob", context.cob_grams);
		push(
			"time_since_last_carb_minutes",
			context.last_carb_at === undefined ? undefined : (atMs - Date.parse(context.last_carb_at)) / 60_000,
		);
	}
	return timelines;
}

/** The engine's replay in the shape the app's replay endpoint returns. */
export function replayResult(
	scenario: Scenario,
	rules: SimRule[],
	ticks: ReplayTick[],
	replayed: ReplayResult,
): AlertReplayResult {
	const byId = new Map(rules.map((r) => [r.id, r]));
	return {
		windowStart: new Date(scenario.start).toISOString(),
		windowEnd: new Date(scenario.end).toISOString(),
		events: replayed.events.flatMap((e) => {
			const kind = EVENT_KINDS[e.kind];
			const rule = byId.get(e.rule_id);
			return kind && rule
				? [{ at: e.at, ruleId: e.rule_id, ruleName: rule.name, severity: SEVERITIES[rule.severity], kind }]
				: [];
		}),
		leafTransitionsByRule: Object.fromEntries(
			replayed.leaf_transitions.map((log) => [
				log.rule_id,
				log.leaves.map((leaf) => ({
					leafId: leaf.leaf_id,
					points: leaf.points.map((p) => ({ atMs: p.at_ms, value: p.value })),
				})),
			]),
		),
		factTimelines: factTimelines(ticks),
	};
}

/** The scenario as the app's chart-data endpoint would return it, transformed as the app does. */
export function chartData(scenario: Scenario): TransformedChartData {
	const series = (of: (s: Scenario, t: number) => number) =>
		tickTimes(scenario).map((t) => ({ timestamp: t, value: Math.round(of(scenario, t) * 100) / 100 }));
	const iobSeries = series(insulinOnBoard);
	const cobSeries = series(carbsOnBoard);
	const dto: DashboardChartData = {
		glucoseData: scenario.readings.map((r) => ({ time: r.at, sgv: r.mgdl })),
		carbMarkers: scenario.carbs.map((c) => ({
			time: c.at,
			carbs: c.grams,
			label: c.label,
			treatmentId: `carbs-${c.at}`,
		})),
		bolusMarkers: scenario.boluses.map((b) => ({ time: b.at, insulin: b.units, treatmentId: `bolus-${b.at}` })),
		iobSeries,
		cobSeries,
		maxIob: Math.max(1, ...iobSeries.map((p) => p.value)),
		maxCob: Math.max(10, ...cobSeries.map((p) => p.value)),
	};
	return transformChartData(dto);
}
