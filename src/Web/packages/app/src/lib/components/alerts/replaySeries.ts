import type { LegendState } from "$lib/components/dashboard/glucose-chart/chart-context.svelte";
import type { ConditionNode } from "./types";

type Series = "iob" | "cob" | "basal" | "bolus" | "carbs";

/**
 * The chart series a leaf reads. Carbs and boluses are drawn inside the
 * IOB/COB track, so a leaf that reads only the markers also opens the track.
 */
const SERIES_BY_LEAF: Partial<Record<string, readonly Series[]>> = {
	iob: ["iob", "bolus"],
	time_since_last_bolus: ["iob", "bolus"],
	cob: ["cob", "carbs"],
	time_since_last_carb: ["cob", "carbs"],
	temp_basal: ["basal"],
	pump_suspended: ["basal"],
	pump_state: ["basal"],
};

function collect(node: ConditionNode, into: Set<Series>): void {
	switch (node.type) {
		case "composite":
			for (const child of node.composite?.conditions ?? []) collect(child, into);
			return;
		case "not":
			if (node.not?.child) collect(node.not.child, into);
			return;
		case "sustained":
			if (node.sustained?.child) collect(node.sustained.child, into);
			return;
		default:
			for (const series of SERIES_BY_LEAF[node.type] ?? []) into.add(series);
	}
}

/**
 * A fixed legend showing glucose and only the series `trees` read, so a replay
 * of a carbs rule draws carbs on board and a glucose-only rule draws glucose
 * alone.
 */
export function legendForRules(trees: Iterable<ConditionNode>): LegendState {
	const shown = new Set<Series>();
	for (const tree of trees) collect(tree, shown);
	return {
		iob: shown.has("iob"),
		cob: shown.has("cob"),
		basal: shown.has("basal"),
		bolus: shown.has("bolus"),
		carbs: shown.has("carbs"),
		pumpModes: shown.has("basal"),
		deviceEvents: false,
		alarms: false,
		scheduledTrackers: false,
		basalInjections: false,
		overrideSpans: false,
		profileSpans: false,
		activitySpans: false,
		expandedPumpModes: false,
		toggle() {},
	};
}
