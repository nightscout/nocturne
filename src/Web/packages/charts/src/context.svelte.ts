import { getContext, setContext } from "svelte";
import type { ChartDataView } from "./view/chart-data-view.svelte.js";
import type { TrackLayout } from "./view/track-layout.js";
import type { ChartFormat, GlucoseUnits } from "./format.js";
import type { ChartLabels } from "./labels.js";

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

/**
 * What a click on the plot asks of the host: `inspect` for a glucose reading, `inspectFromTrack`
 * for a time on another track. The host decides what opens.
 */
export interface ChartInspector {
	inspect(time: Date, glucosePoint: { sgv: number; color: string }, dataSource?: string): void;
	inspectFromTrack(time: Date): void;
}

export interface GlucoseChartContext {
	readonly view: ChartDataView;
	readonly layout: TrackLayout;
	readonly inspection?: ChartInspector;
	readonly legend?: LegendState;
	/**
	 * The chart is laid out for paper: tracks trade on-plot labels and colour
	 * for gutter labels, larger text and ink.
	 */
	readonly printing: boolean;
	readonly glucoseUnits: GlucoseUnits;
	readonly labels: ChartLabels;
	readonly format: ChartFormat;
}

const GLUCOSE_CHART_KEY = "nocturne:charts:glucose-chart";

export function setGlucoseChartContext(value: GlucoseChartContext) {
	return setContext(GLUCOSE_CHART_KEY, value);
}

export function getGlucoseChartContext(): GlucoseChartContext {
	const chart = getContext<GlucoseChartContext | undefined>(GLUCOSE_CHART_KEY);
	if (!chart) throw new Error("Glucose chart context not found");
	return chart;
}

/**
 * A marker icon's colour, or ink on paper: an icon drawn in its span's or
 * event's colour prints grey on grey. Its shape is what the key names.
 */
export function markerInk(chart: Pick<GlucoseChartContext, "printing">, color: string): string {
	return chart.printing ? "var(--foreground)" : color;
}

/**
 * Stands a partial view in for the full one, for a host that renders only
 * tracks reading the fields it supplies (the calendar sparkline, test
 * harnesses). The context is typed for the whole chart, so this is asserted:
 * a track that starts reading a field the host omits sees `undefined` at
 * runtime, not a type error — keep the host's field list in step with its tracks.
 */
export function partialView<K extends keyof ChartDataView>(
	fields: Pick<ChartDataView, K>
): ChartDataView {
	// eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- see the doc comment: a track-only host has no full view to give
	return fields as ChartDataView;
}
