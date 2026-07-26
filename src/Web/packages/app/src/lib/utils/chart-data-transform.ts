import type { DashboardChartData } from '$lib/api/generated/nocturne-api-client';
import { resolveChartColor, getGlucoseColor } from '$lib/utils/chart-colors';
import { resolveGlucoseThresholds } from '$lib/constants/glucose-thresholds';

/**
 * Transform raw NSwag DashboardChartData into the shape consumed by chart components.
 * Converts mills timestamps to Date objects and resolves chart colors to CSS variables.
 * Glucose values are kept in mg/dL — unit conversion to mmol/L is applied at the
 * display layer (Y-axis labels, tooltips) via bg() on the client.
 *
 * This is the single source of truth for API → chart component transformation.
 * Used by both the remote function (client-side fetch) and SSR page load.
 */
export function transformChartData(data: DashboardChartData) {
	const mapSpans = (spans: typeof data.pumpModeSpans) =>
		(spans ?? []).map((s) => ({
			id: s.id,
			kind: s.kind,
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

		// A server-side 0 means "no profile yet for that historical instant";
		// `resolveGlucoseThresholds` and the `||` below treat it as missing so
		// threshold lines aren't pinned at the axis.
		glucoseData: (data.glucoseData ?? []).map((p) => ({
			time: new Date(p.time ?? 0),
			sgv: p.sgv ?? 0,
			direction: p.direction,
			dataSource: p.dataSource,
			color: getGlucoseColor(
				p.sgv ?? 0,
				resolveGlucoseThresholds(data.thresholds)
			),
		})),
		thresholds: {
			...resolveGlucoseThresholds(data.thresholds),
			glucoseYMax: data.thresholds?.glucoseYMax || 300,
			// Personal target reference line; null when no profile is available.
			targetLow: data.thresholds?.targetLow ?? null,
			targetHigh: data.thresholds?.targetHigh ?? null,
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

		basalInjectionMarkers: (data.basalInjectionMarkers ?? []).map((m) => ({
			id: m.id ?? '',
			time: new Date(m.time ?? 0),
			units: m.units ?? 0,
			insulinName: m.insulinName,
		})),

		bgCheckMarkers: (data.bgCheckMarkers ?? []).map((m) => ({
			time: new Date(m.time ?? 0),
			glucose: m.glucose ?? 0,
			glucoseType: m.glucoseType,
			treatmentId: m.treatmentId,
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

		heartRateSeries: (data.heartRateSeries ?? []).map((p) => ({
			time: new Date(p.time ?? 0),
			bpm: p.bpm ?? 0,
		})),
		stepSeries: (data.stepSeries ?? []).map((p) => ({
			time: new Date(p.time ?? 0),
			steps: p.steps ?? 0,
		})),
	};
}

export type TransformedChartData = ReturnType<typeof transformChartData>;
