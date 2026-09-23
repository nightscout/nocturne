import { Context } from "runed";
import type { ChartDataEngine } from "./engine/chart-data-engine.svelte";
import type { TrackLayout } from "./engine/track-layout";
import type { PointInspection } from "./engine/point-inspection.svelte";

export interface LegendState {
	readonly iob: boolean;
	readonly cob: boolean;
	readonly basal: boolean;
	readonly bolus: boolean;
	readonly carbs: boolean;
	readonly deviceEvents: boolean;
	readonly alarms: boolean;
	readonly scheduledTrackers: boolean;
	readonly basalInjections: boolean;
	readonly overrideSpans: boolean;
	readonly profileSpans: boolean;
	readonly activitySpans: boolean;
	readonly pumpModes: boolean;
	readonly expandedPumpModes: boolean;
	toggle(key: string): void;
}

export interface GlucoseChartContext {
	readonly engine: ChartDataEngine;
	readonly layout: TrackLayout;
	readonly inspection?: PointInspection;
	readonly legend?: LegendState;
}

const ctx = new Context<GlucoseChartContext>("GlucoseChartContext");

export function setGlucoseChartContext(value: GlucoseChartContext) {
	return ctx.set(value);
}

export function getGlucoseChartContext(): GlucoseChartContext {
	return ctx.get();
}

/**
 * Stands a partial engine in for the full one, for a host that renders only
 * tracks reading the fields it supplies (the calendar sparkline, test
 * harnesses). The context is typed for the whole chart, so this is asserted:
 * a track that starts reading a field the host omits sees `undefined` at
 * runtime, not a type error — keep the host's field list in step with its tracks.
 */
export function partialEngine<K extends keyof ChartDataEngine>(
	fields: Pick<ChartDataEngine, K>
): ChartDataEngine {
	// eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- see the doc comment: a track-only host has no full engine to give
	return fields as ChartDataEngine;
}
