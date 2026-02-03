/**
 * Remote functions for state span and system event data
 * Provides pre-processed data for chart visualizations
 */
import { getRequestEvent, query } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';
import { StateSpanCategory, SystemEventType, SystemEventCategory, BasalDeliveryOrigin } from '$lib/api';

/**
 * Input schema for state data queries
 */
const stateDataSchema = z.object({
	startTime: z.number(),
	endTime: z.number(),
});

export type StateDataInput = z.infer<typeof stateDataSchema>;

/**
 * Processed state span for chart rendering
 */
export interface StateSpanChartData {
	id: string;
	category: StateSpanCategory;
	state: string;
	startTime: Date;
	endTime: Date | null;
	color: string;
	metadata?: Record<string, unknown>;
}

/**
 * Processed system event for chart rendering
 */
export interface SystemEventChartData {
	id: string;
	eventType: SystemEventType;
	category: SystemEventCategory;
	time: Date;
	code?: string;
	description?: string;
	color: string;
}

/**
 * Processed basal delivery span for chart rendering
 */
export interface BasalDeliveryChartData {
	id: string;
	startTime: Date;
	endTime: Date | null;
	rate: number;
	origin: BasalDeliveryOrigin;
	source?: string;
	color: string;
}

/**
 * Combined state data response for charts
 */
export interface ChartStateData {
	pumpModeSpans: StateSpanChartData[];
	connectivitySpans: StateSpanChartData[];
	tempBasalSpans: StateSpanChartData[];
	overrideSpans: StateSpanChartData[];
	profileSpans: StateSpanChartData[];
	activitySpans: StateSpanChartData[];
	basalDeliverySpans: BasalDeliveryChartData[];
	dataExclusionSpans: StateSpanChartData[];
	systemEvents: SystemEventChartData[];
}

/**
 * Map pump mode state to CSS color variable
 */
function getPumpModeColor(state: string): string {
	const stateColors: Record<string, string> = {
		'Automatic': 'var(--pump-mode-automatic)',
		'Limited': 'var(--pump-mode-limited)',
		'Manual': 'var(--pump-mode-manual)',
		'Boost': 'var(--pump-mode-boost)',
		'EaseOff': 'var(--pump-mode-ease-off)',
		'Sleep': 'var(--pump-mode-sleep)',
		'Exercise': 'var(--pump-mode-exercise)',
		'Suspended': 'var(--pump-mode-suspended)',
		'Off': 'var(--pump-mode-off)',
	};
	return stateColors[state] ?? 'var(--pump-mode-manual)';
}

/**
 * Map connectivity state to CSS color variable
 */
function getConnectivityColor(state: string): string {
	const stateColors: Record<string, string> = {
		'Connected': 'var(--pump-mode-automatic)',
		'Disconnected': 'var(--pump-mode-suspended)',
		'Removed': 'var(--pump-mode-off)',
		'BluetoothOff': 'var(--pump-mode-off)',
	};
	return stateColors[state] ?? 'var(--pump-mode-off)';
}

/**
 * Map system event type to CSS color variable
 */
function getSystemEventColor(eventType: SystemEventType): string {
	const typeColors: Record<SystemEventType, string> = {
		[SystemEventType.Alarm]: 'var(--system-event-alarm)',
		[SystemEventType.Hazard]: 'var(--system-event-hazard)',
		[SystemEventType.Warning]: 'var(--system-event-warning)',
		[SystemEventType.Info]: 'var(--system-event-info)',
	};
	return typeColors[eventType] ?? 'var(--system-event-info)';
}

/**
 * Map temp basal state to CSS color variable
 */
function getTempBasalColor(_state: string): string {
	return 'var(--insulin-basal)';
}

/**
 * Map override state to CSS color variable
 */
function getOverrideColor(state: string): string {
	const stateColors: Record<string, string> = {
		Boost: 'var(--pump-mode-boost)',
		Exercise: 'var(--pump-mode-exercise)',
		Sleep: 'var(--pump-mode-sleep)',
		EaseOff: 'var(--pump-mode-ease-off)',
	};
	return stateColors[state] ?? 'var(--chart-2)';
}

/**
 * Map profile state to CSS color variable
 */
function getProfileColor(_state: string): string {
	return 'var(--chart-1)';
}

/**
 * Map activity state (sleep, exercise, illness, travel) to CSS color variable
 */
function getActivityColor(category: StateSpanCategory, _state: string): string {
	const categoryColors: Record<string, string> = {
		[StateSpanCategory.Sleep]: 'var(--pump-mode-sleep)',
		[StateSpanCategory.Exercise]: 'var(--pump-mode-exercise)',
		[StateSpanCategory.Illness]: 'var(--system-event-warning)',
		[StateSpanCategory.Travel]: 'var(--chart-3)',
	};
	return categoryColors[category] ?? 'var(--muted-foreground)';
}

/**
 * Map basal delivery origin to CSS color variable
 */
function getBasalDeliveryColor(origin: BasalDeliveryOrigin): string {
	const originColors: Record<BasalDeliveryOrigin, string> = {
		[BasalDeliveryOrigin.Algorithm]: 'var(--insulin-basal)',
		[BasalDeliveryOrigin.Scheduled]: 'var(--insulin-basal)',
		[BasalDeliveryOrigin.Manual]: 'var(--insulin-temp-basal)',
		[BasalDeliveryOrigin.Suspended]: 'var(--pump-mode-suspended)',
		[BasalDeliveryOrigin.Inferred]: 'var(--insulin-temp-basal)',
	};
	return originColors[origin] ?? 'var(--insulin-basal)';
}

/**
 * Map data exclusion state to CSS color variable
 */
function getDataExclusionColor(state: string): string {
	const stateColors: Record<string, string> = {
		CompressionLow: 'var(--data-exclusion-compression-low)',
		SensorError: 'var(--data-exclusion-sensor-error)',
	};
	return stateColors[state] ?? 'var(--muted-foreground)';
}

/**
 * Get state span and system event data for chart visualization
 */
export const getChartStateData = query(stateDataSchema, async ({ startTime, endTime }) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		// Fetch all state spans in parallel
		const [
			pumpModeSpans,
			connectivitySpans,
			overrideSpans,
			profileSpans,
			activitySpans,
			basalDeliverySpans,
			dataExclusionSpans,
			systemEvents,
		] = await Promise.all([
			apiClient.stateSpans.getPumpModes(startTime, endTime),
			apiClient.stateSpans.getConnectivity(startTime, endTime),
			apiClient.stateSpans.getOverrides(startTime, endTime),
			apiClient.stateSpans.getProfiles(startTime, endTime),
			apiClient.stateSpans.getActivities(startTime, endTime),
			apiClient.stateSpans.getStateSpans(
				StateSpanCategory.BasalDelivery,
				undefined, // state - get all
				startTime,
				endTime
			),
			apiClient.stateSpans.getStateSpans(
				StateSpanCategory.DataExclusion,
				undefined, // state - get all
				startTime,
				endTime
			),
			apiClient.systemEvents.getSystemEvents(
				undefined, // type - get all
				undefined, // category - get all
				startTime,
				endTime
			),
		]);

		// Transform pump mode spans
		const processedPumpModes: StateSpanChartData[] = (pumpModeSpans ?? []).map((span) => ({
			id: span.id ?? '',
			category: span.category ?? StateSpanCategory.PumpMode,
			state: span.state ?? 'Unknown',
			startTime: new Date(span.startMills ?? 0),
			endTime: span.endMills ? new Date(span.endMills) : null,
			color: getPumpModeColor(span.state ?? ''),
			metadata: span.metadata,
		}));

		// Transform connectivity spans
		const processedConnectivity: StateSpanChartData[] = (connectivitySpans ?? []).map((span) => ({
			id: span.id ?? '',
			category: span.category ?? StateSpanCategory.PumpConnectivity,
			state: span.state ?? 'Unknown',
			startTime: new Date(span.startMills ?? 0),
			endTime: span.endMills ? new Date(span.endMills) : null,
			color: getConnectivityColor(span.state ?? ''),
			metadata: span.metadata,
		}));

		// Derive temp basal spans from basal delivery spans with Manual origin
		const processedTempBasals: StateSpanChartData[] = (basalDeliverySpans ?? [])
			.filter((span) => (span.metadata?.origin as BasalDeliveryOrigin) === BasalDeliveryOrigin.Manual)
			.map((span) => ({
				id: span.id ?? '',
				category: StateSpanCategory.BasalDelivery,
				state: 'TempBasal',
				startTime: new Date(span.startMills ?? 0),
				endTime: span.endMills ? new Date(span.endMills) : null,
				color: getTempBasalColor('TempBasal'),
				metadata: span.metadata,
			}));

		// Transform override spans
		const processedOverrides: StateSpanChartData[] = (overrideSpans ?? []).map((span) => ({
			id: span.id ?? '',
			category: span.category ?? StateSpanCategory.Override,
			state: span.state ?? 'Unknown',
			startTime: new Date(span.startMills ?? 0),
			endTime: span.endMills ? new Date(span.endMills) : null,
			color: getOverrideColor(span.state ?? ''),
			metadata: span.metadata,
		}));

		// Transform profile spans
		const processedProfiles: StateSpanChartData[] = (profileSpans ?? []).map((span) => ({
			id: span.id ?? '',
			category: span.category ?? StateSpanCategory.Profile,
			state: span.state ?? 'Unknown',
			startTime: new Date(span.startMills ?? 0),
			endTime: span.endMills ? new Date(span.endMills) : null,
			color: getProfileColor(span.state ?? ''),
			metadata: span.metadata,
		}));

		// Transform activity spans (sleep, exercise, illness, travel)
		const processedActivities: StateSpanChartData[] = (activitySpans ?? []).map((span) => ({
			id: span.id ?? '',
			category: span.category ?? StateSpanCategory.Sleep,
			state: span.state ?? 'Unknown',
			startTime: new Date(span.startMills ?? 0),
			endTime: span.endMills ? new Date(span.endMills) : null,
			color: getActivityColor(span.category ?? StateSpanCategory.Sleep, span.state ?? ''),
			metadata: span.metadata,
		}));

		// Transform system events
		const processedEvents: SystemEventChartData[] = (systemEvents ?? []).map((event) => ({
			id: event.id ?? '',
			eventType: event.eventType ?? SystemEventType.Info,
			category: event.category ?? SystemEventCategory.Pump,
			time: new Date(event.mills ?? 0),
			code: event.code,
			description: event.description,
			color: getSystemEventColor(event.eventType ?? SystemEventType.Info),
		}));

		// Transform basal delivery spans
		const processedBasalDelivery: BasalDeliveryChartData[] = (basalDeliverySpans ?? []).map((span) => {
			const origin = (span.metadata?.origin as BasalDeliveryOrigin) ?? BasalDeliveryOrigin.Scheduled;
			return {
				id: span.id ?? '',
				startTime: new Date(span.startMills ?? 0),
				endTime: span.endMills ? new Date(span.endMills) : null,
				rate: (span.metadata?.rate as number) ?? 0,
				origin,
				source: span.source,
				color: getBasalDeliveryColor(origin),
			};
		});

		// Transform data exclusion spans (compression lows, sensor errors, etc.)
		const processedDataExclusions: StateSpanChartData[] = (dataExclusionSpans ?? []).map((span) => ({
			id: span.id ?? '',
			category: span.category ?? StateSpanCategory.DataExclusion,
			state: span.state ?? 'Unknown',
			startTime: new Date(span.startMills ?? 0),
			endTime: span.endMills ? new Date(span.endMills) : null,
			color: getDataExclusionColor(span.state ?? ''),
			metadata: span.metadata,
		}));

		return {
			pumpModeSpans: processedPumpModes,
			connectivitySpans: processedConnectivity,
			tempBasalSpans: processedTempBasals,
			overrideSpans: processedOverrides,
			profileSpans: processedProfiles,
			activitySpans: processedActivities,
			basalDeliverySpans: processedBasalDelivery,
			dataExclusionSpans: processedDataExclusions,
			systemEvents: processedEvents,
		};
	} catch (err) {
		console.error('Error loading state data:', err);
		throw error(500, 'Failed to load state data');
	}
});
