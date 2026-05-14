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
