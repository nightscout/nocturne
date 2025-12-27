/**
 * Remote functions for state span and system event data
 * Provides pre-processed data for chart visualizations
 */
import { getRequestEvent, query } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';
import { StateSpanCategory, SystemEventType, SystemEventCategory } from '$lib/api';

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
 * Combined state data response for charts
 */
export interface ChartStateData {
	pumpModeSpans: StateSpanChartData[];
	connectivitySpans: StateSpanChartData[];
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
 * Get state span and system event data for chart visualization
 */
export const getChartStateData = query(stateDataSchema, async ({ startTime, endTime }) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		// Fetch pump mode spans
		const pumpModeSpans = await apiClient.stateSpans.getPumpModes(startTime, endTime);

		// Fetch connectivity spans
		const connectivitySpans = await apiClient.stateSpans.getConnectivity(startTime, endTime);

		// Fetch system events
		const systemEvents = await apiClient.systemEvents.getSystemEvents(
			undefined, // type - get all
			undefined, // category - get all
			startTime,
			endTime
		);

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

		return {
			pumpModeSpans: processedPumpModes,
			connectivitySpans: processedConnectivity,
			systemEvents: processedEvents,
		} satisfies ChartStateData;
	} catch (err) {
		console.error('Error loading state data:', err);
		throw error(500, 'Failed to load state data');
	}
});
