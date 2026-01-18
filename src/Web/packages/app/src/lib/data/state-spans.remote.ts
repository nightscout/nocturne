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
	tempBasalSpans: StateSpanChartData[];
	overrideSpans: StateSpanChartData[];
	profileSpans: StateSpanChartData[];
	activitySpans: StateSpanChartData[];
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
			tempBasalSpans,
			overrideSpans,
			profileSpans,
			activitySpans,
			systemEvents,
		] = await Promise.all([
			apiClient.stateSpans.getPumpModes(startTime, endTime),
			apiClient.stateSpans.getConnectivity(startTime, endTime),
			apiClient.stateSpans.getTempBasals(startTime, endTime),
			apiClient.stateSpans.getOverrides(startTime, endTime),
			apiClient.stateSpans.getProfiles(startTime, endTime),
			apiClient.stateSpans.getActivities(startTime, endTime),
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

		// Transform temp basal spans
		const processedTempBasals: StateSpanChartData[] = (tempBasalSpans ?? []).map((span) => ({
			id: span.id ?? '',
			category: span.category ?? StateSpanCategory.TempBasal,
			state: span.state ?? 'Unknown',
			startTime: new Date(span.startMills ?? 0),
			endTime: span.endMills ? new Date(span.endMills) : null,
			color: getTempBasalColor(span.state ?? ''),
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

		// ============================================================
		// TODO: DELETE THIS TEST DATA BLOCK AFTER TESTING
		// ============================================================
		const testData = generateTestStateSpans(startTime, endTime);

		return {
			pumpModeSpans: [...processedPumpModes, ...testData.pumpModeSpans],
			connectivitySpans: processedConnectivity,
			tempBasalSpans: [...processedTempBasals, ...testData.tempBasalSpans],
			overrideSpans: [...processedOverrides, ...testData.overrideSpans],
			profileSpans: [...processedProfiles, ...testData.profileSpans],
			activitySpans: [...processedActivities, ...testData.activitySpans],
			systemEvents: processedEvents,
		} satisfies ChartStateData;
		// ============================================================
		// END TEST DATA BLOCK
		// ============================================================
	} catch (err) {
		console.error('Error loading state data:', err);
		throw error(500, 'Failed to load state data');
	}
});

// ============================================================
// ============================================================
// TODO: DELETE EVERYTHING BELOW THIS LINE AFTER TESTING
// ============================================================
// ============================================================

/**
 * Generate test state spans for the last 24 hours
 * DELETE THIS FUNCTION AFTER TESTING
 */
function generateTestStateSpans(startTime: number, endTime: number): ChartStateData {
	const now = Date.now();
	const dayAgo = now - 24 * 60 * 60 * 1000;

	// Only generate test data if the range includes the last 24 hours
	const rangeStart = Math.max(startTime, dayAgo);
	const rangeEnd = Math.min(endTime, now);

	if (rangeStart >= rangeEnd) {
		return {
			pumpModeSpans: [],
			connectivitySpans: [],
			tempBasalSpans: [],
			overrideSpans: [],
			profileSpans: [],
			activitySpans: [],
			systemEvents: [],
		};
	}

	const hour = 60 * 60 * 1000;

	// Generate pump mode spans (covering the full 24h period)
	const pumpModeSpans: StateSpanChartData[] = [
		{
			id: 'test-pump-auto-1',
			category: StateSpanCategory.PumpMode,
			state: 'Automatic',
			startTime: new Date(dayAgo),
			endTime: new Date(dayAgo + 6 * hour),
			color: getPumpModeColor('Automatic'),
		},
		{
			id: 'test-pump-limited-1',
			category: StateSpanCategory.PumpMode,
			state: 'Limited',
			startTime: new Date(dayAgo + 6 * hour),
			endTime: new Date(dayAgo + 8 * hour),
			color: getPumpModeColor('Limited'),
		},
		{
			id: 'test-pump-auto-2',
			category: StateSpanCategory.PumpMode,
			state: 'Automatic',
			startTime: new Date(dayAgo + 8 * hour),
			endTime: new Date(dayAgo + 14 * hour),
			color: getPumpModeColor('Automatic'),
		},
		{
			id: 'test-pump-suspended-1',
			category: StateSpanCategory.PumpMode,
			state: 'Suspended',
			startTime: new Date(dayAgo + 14 * hour),
			endTime: new Date(dayAgo + 14.5 * hour),
			color: getPumpModeColor('Suspended'),
		},
		{
			id: 'test-pump-auto-3',
			category: StateSpanCategory.PumpMode,
			state: 'Automatic',
			startTime: new Date(dayAgo + 14.5 * hour),
			endTime: new Date(now),
			color: getPumpModeColor('Automatic'),
		},
	];

	// Generate temp basal spans (scattered throughout)
	const tempBasalSpans: StateSpanChartData[] = [
		{
			id: 'test-temp-1',
			category: StateSpanCategory.TempBasal,
			state: 'TempBasal',
			startTime: new Date(dayAgo + 2 * hour),
			endTime: new Date(dayAgo + 2.5 * hour),
			color: getTempBasalColor('TempBasal'),
			metadata: { rate: 0.8 },
		},
		{
			id: 'test-temp-2',
			category: StateSpanCategory.TempBasal,
			state: 'TempBasal',
			startTime: new Date(dayAgo + 5 * hour),
			endTime: new Date(dayAgo + 6 * hour),
			color: getTempBasalColor('TempBasal'),
			metadata: { rate: 1.5 },
		},
		{
			id: 'test-temp-3',
			category: StateSpanCategory.TempBasal,
			state: 'TempBasal',
			startTime: new Date(dayAgo + 10 * hour),
			endTime: new Date(dayAgo + 10.75 * hour),
			color: getTempBasalColor('TempBasal'),
			metadata: { percent: 150 },
		},
		{
			id: 'test-temp-4',
			category: StateSpanCategory.TempBasal,
			state: 'TempBasal',
			startTime: new Date(dayAgo + 16 * hour),
			endTime: new Date(dayAgo + 17 * hour),
			color: getTempBasalColor('TempBasal'),
			metadata: { rate: 0.3 },
		},
		{
			id: 'test-temp-5',
			category: StateSpanCategory.TempBasal,
			state: 'TempBasal',
			startTime: new Date(dayAgo + 20 * hour),
			endTime: new Date(dayAgo + 21 * hour),
			color: getTempBasalColor('TempBasal'),
			metadata: { rate: 2.0 },
		},
	];

	// Generate override spans
	const overrideSpans: StateSpanChartData[] = [
		{
			id: 'test-override-boost-1',
			category: StateSpanCategory.Override,
			state: 'Boost',
			startTime: new Date(dayAgo + 7 * hour),
			endTime: new Date(dayAgo + 8 * hour),
			color: getOverrideColor('Boost'),
		},
		{
			id: 'test-override-exercise-1',
			category: StateSpanCategory.Override,
			state: 'Exercise',
			startTime: new Date(dayAgo + 12 * hour),
			endTime: new Date(dayAgo + 13 * hour),
			color: getOverrideColor('Exercise'),
		},
		{
			id: 'test-override-sleep-1',
			category: StateSpanCategory.Override,
			state: 'Sleep',
			startTime: new Date(dayAgo + 22 * hour),
			endTime: new Date(now - 2 * hour),
			color: getOverrideColor('Sleep'),
		},
	];

	// Generate profile spans
	const profileSpans: StateSpanChartData[] = [
		{
			id: 'test-profile-default',
			category: StateSpanCategory.Profile,
			state: 'Default',
			startTime: new Date(dayAgo),
			endTime: new Date(dayAgo + 12 * hour),
			color: getProfileColor('Default'),
			metadata: { profileName: 'Default' },
		},
		{
			id: 'test-profile-workday',
			category: StateSpanCategory.Profile,
			state: 'Workday',
			startTime: new Date(dayAgo + 12 * hour),
			endTime: new Date(dayAgo + 20 * hour),
			color: getProfileColor('Workday'),
			metadata: { profileName: 'Workday' },
		},
		{
			id: 'test-profile-default-2',
			category: StateSpanCategory.Profile,
			state: 'Default',
			startTime: new Date(dayAgo + 20 * hour),
			endTime: new Date(now),
			color: getProfileColor('Default'),
			metadata: { profileName: 'Default' },
		},
	];

	// Generate activity spans (sleep, exercise, illness, travel)
	const activitySpans: StateSpanChartData[] = [
		// Sleep period (night before)
		{
			id: 'test-activity-sleep-1',
			category: StateSpanCategory.Sleep,
			state: 'Sleeping',
			startTime: new Date(dayAgo),
			endTime: new Date(dayAgo + 6 * hour),
			color: getActivityColor(StateSpanCategory.Sleep, 'Sleeping'),
		},
		// Morning exercise
		{
			id: 'test-activity-exercise-1',
			category: StateSpanCategory.Exercise,
			state: 'Running',
			startTime: new Date(dayAgo + 7 * hour),
			endTime: new Date(dayAgo + 8 * hour),
			color: getActivityColor(StateSpanCategory.Exercise, 'Running'),
		},
		// Afternoon exercise
		{
			id: 'test-activity-exercise-2',
			category: StateSpanCategory.Exercise,
			state: 'Gym',
			startTime: new Date(dayAgo + 17 * hour),
			endTime: new Date(dayAgo + 18.5 * hour),
			color: getActivityColor(StateSpanCategory.Exercise, 'Gym'),
		},
		// Travel period
		{
			id: 'test-activity-travel-1',
			category: StateSpanCategory.Travel,
			state: 'Flying',
			startTime: new Date(dayAgo + 9 * hour),
			endTime: new Date(dayAgo + 11 * hour),
			color: getActivityColor(StateSpanCategory.Travel, 'Flying'),
		},
		// Sleep period (tonight)
		{
			id: 'test-activity-sleep-2',
			category: StateSpanCategory.Sleep,
			state: 'Sleeping',
			startTime: new Date(dayAgo + 22 * hour),
			endTime: new Date(now - 2 * hour),
			color: getActivityColor(StateSpanCategory.Sleep, 'Sleeping'),
		},
	];

	return {
		pumpModeSpans,
		connectivitySpans: [],
		tempBasalSpans,
		overrideSpans,
		profileSpans,
		activitySpans,
		systemEvents: [],
	};
}

// ============================================================
// END OF TEST CODE - DELETE EVERYTHING ABOVE UP TO THE MARKED LINE
// ============================================================
