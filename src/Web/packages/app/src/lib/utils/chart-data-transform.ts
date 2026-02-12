import type { DashboardChartData } from '$lib/api/generated/nocturne-api-client';
import { resolveChartColor, getGlucoseColor } from '$lib/utils/chart-colors';
import { bg } from '$lib/utils/formatting';

/**
 * Transform raw NSwag DashboardChartData into the shape consumed by chart components.
 * Converts mills timestamps to Date objects, resolves chart colors to CSS variables,
 * and applies unit conversion via bg().
 *
 * This is the single source of truth for API → chart component transformation.
 * Used by both the remote function (client-side fetch) and SSR page load.
 */
export function transformChartData(data: DashboardChartData) {
	const mapSpans = (spans: typeof data.pumpModeSpans) =>
		(spans ?? []).map((s) => ({
			id: s.id,
			category: s.category,
			state: s.state,
			startTime: new Date(s.startMills ?? 0),
			endTime: s.endMills != null ? new Date(s.endMills) : null,
			color: resolveChartColor(s.color ?? 'muted-foreground'),
			metadata: s.metadata,
		}));

	return {
		iobSeries: (data.iobSeries ?? []).map((p) => ({
			time: new Date(p.timestamp ?? 0),
			value: p.value ?? 0,
		})),
		cobSeries: (data.cobSeries ?? []).map((p) => ({
			time: new Date(p.timestamp ?? 0),
			value: p.value ?? 0,
		})),
		basalSeries: (data.basalSeries ?? []).map((p) => ({
			timestamp: p.timestamp,
			rate: p.rate,
			scheduledRate: p.scheduledRate,
			origin: p.origin,
			fillColor: resolveChartColor(p.fillColor ?? 'insulin-basal'),
			strokeColor: resolveChartColor(p.strokeColor ?? 'insulin-basal'),
		})),
		defaultBasalRate: data.defaultBasalRate ?? 1.0,
		maxBasalRate: data.maxBasalRate ?? 3.0,
		maxIob: data.maxIob ?? 5.0,
		maxCob: data.maxCob ?? 100.0,

		glucoseData: (data.glucoseData ?? []).map((p) => ({
			time: new Date(p.time ?? 0),
			sgv: Number(bg(p.sgv ?? 0)),
			direction: p.direction,
			color: getGlucoseColor(p.sgv ?? 0, {
				low: data.thresholds?.low ?? 55,
				high: data.thresholds?.high ?? 180,
				veryLow: data.thresholds?.veryLow ?? 54,
				veryHigh: data.thresholds?.veryHigh ?? 250,
			}),
		})),
		thresholds: {
			low: Number(bg(data.thresholds?.low ?? 55)),
			high: Number(bg(data.thresholds?.high ?? 180)),
			veryLow: Number(bg(data.thresholds?.veryLow ?? 54)),
			veryHigh: Number(bg(data.thresholds?.veryHigh ?? 250)),
			glucoseYMax: Number(bg(data.thresholds?.glucoseYMax ?? 300)),
		},

		bolusMarkers: (data.bolusMarkers ?? []).map((m) => ({
			...m,
			time: new Date(m.time ?? 0),
		})),
		carbMarkers: (data.carbMarkers ?? []).map((m) => ({
			...m,
			time: new Date(m.time ?? 0),
		})),
		deviceEventMarkers: (data.deviceEventMarkers ?? []).map((m) => ({
			...m,
			time: new Date(m.time ?? 0),
			color: resolveChartColor(m.color ?? 'muted-foreground'),
		})),

		pumpModeSpans: mapSpans(data.pumpModeSpans),
		profileSpans: mapSpans(data.profileSpans),
		overrideSpans: mapSpans(data.overrideSpans),
		activitySpans: mapSpans(data.activitySpans),
		tempBasalSpans: mapSpans(data.tempBasalSpans),
		basalDeliverySpans: (data.basalDeliverySpans ?? []).map((s) => ({
			...s,
			startTime: new Date(s.startMills ?? 0),
			endTime: s.endMills != null ? new Date(s.endMills) : null,
			fillColor: resolveChartColor(s.fillColor ?? 'insulin-basal'),
			strokeColor: resolveChartColor(s.strokeColor ?? 'insulin-basal'),
		})),

		systemEventMarkers: (data.systemEventMarkers ?? []).map((e) => ({
			...e,
			time: new Date(e.time ?? 0),
			color: resolveChartColor(e.color ?? 'muted-foreground'),
		})),

		trackerMarkers: (data.trackerMarkers ?? []).map((t) => ({
			...t,
			time: new Date(t.time ?? 0),
			color: resolveChartColor(t.color ?? 'muted-foreground'),
		})),
	};
}

export type TransformedChartData = ReturnType<typeof transformChartData>;
