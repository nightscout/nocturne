import type { TransformedChartData } from '$lib/utils/chart-data-transform';

/**
 * Merge historical chart data into initial chart data.
 * Historical data contains older records that should be prepended to arrays.
 */
export function mergeChartData(
	initial: TransformedChartData,
	historical: TransformedChartData | null
): TransformedChartData {
	if (!historical) return initial;

	// Helper to merge arrays by time, avoiding duplicates
	const mergeByTime = <T extends Record<string, any>>(
		initialArr: T[],
		historicalArr: T[],
		timeKey: string = 'time'
	): T[] => {
		if (!initialArr || !historicalArr) return initialArr || historicalArr || [];
		const initialTimes = new Set(
			initialArr.map((item) => item[timeKey]?.getTime?.() ?? item[timeKey])
		);
		const uniqueHistorical = historicalArr.filter((item) => {
			const time = item[timeKey]?.getTime?.() ?? item[timeKey];
			return !initialTimes.has(time);
		});
		return [...uniqueHistorical, ...initialArr];
	};

	return {
		...initial,
		// Merge time-series data
		iobSeries: mergeByTime(initial.iobSeries, historical.iobSeries),
		cobSeries: mergeByTime(initial.cobSeries, historical.cobSeries),
		basalSeries: mergeByTime(initial.basalSeries, historical.basalSeries, 'timestamp'),
		glucoseData: mergeByTime(initial.glucoseData, historical.glucoseData),

		// Merge markers
		bolusMarkers: mergeByTime(initial.bolusMarkers, historical.bolusMarkers),
		carbMarkers: mergeByTime(initial.carbMarkers, historical.carbMarkers),
		deviceEventMarkers: mergeByTime(initial.deviceEventMarkers, historical.deviceEventMarkers),
		systemEventMarkers: mergeByTime(initial.systemEventMarkers, historical.systemEventMarkers),
		trackerMarkers: mergeByTime(initial.trackerMarkers, historical.trackerMarkers),

		// Merge spans
		pumpModeSpans: mergeByTime(initial.pumpModeSpans, historical.pumpModeSpans, 'startTime'),
		profileSpans: mergeByTime(initial.profileSpans, historical.profileSpans, 'startTime'),
		overrideSpans: mergeByTime(initial.overrideSpans, historical.overrideSpans, 'startTime'),
		activitySpans: mergeByTime(initial.activitySpans, historical.activitySpans, 'startTime'),
		tempBasalSpans: mergeByTime(initial.tempBasalSpans, historical.tempBasalSpans, 'startTime'),
		basalDeliverySpans: mergeByTime(
			initial.basalDeliverySpans,
			historical.basalDeliverySpans,
			'startTime'
		),

		// Take the max values from either dataset
		maxIob: Math.max(initial.maxIob ?? 0, historical.maxIob ?? 0),
		maxCob: Math.max(initial.maxCob ?? 0, historical.maxCob ?? 0),
		maxBasalRate: Math.max(initial.maxBasalRate ?? 0, historical.maxBasalRate ?? 0),
	};
}
