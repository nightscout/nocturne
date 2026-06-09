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

	// Helper to merge span arrays by id, falling back to startTime dedup.
	// Spans that straddle the initial/historical boundary can appear in both
	// datasets with the same id but different startTime, so time-based dedup
	// alone would let duplicates through and cause Svelte each_key_duplicate.
	const mergeSpansById = <T extends Record<string, any>>(
		initialArr: T[],
		historicalArr: T[]
	): T[] => {
		if (!initialArr || !historicalArr) return initialArr || historicalArr || [];
		const seenIds = new Set(
			initialArr.map((item) => item.id).filter((id: unknown) => id != null)
		);
		const uniqueHistorical = historicalArr.filter((item) => {
			if (item.id != null) {
				if (seenIds.has(item.id)) return false;
				seenIds.add(item.id);
				return true;
			}
			// No id — fall back to startTime dedup
			return true;
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

		// Merge markers (keyed by time)
		bolusMarkers: mergeByTime(initial.bolusMarkers, historical.bolusMarkers),
		carbMarkers: mergeByTime(initial.carbMarkers, historical.carbMarkers),
		deviceEventMarkers: mergeByTime(initial.deviceEventMarkers, historical.deviceEventMarkers),
		systemEventMarkers: mergeSpansById(initial.systemEventMarkers, historical.systemEventMarkers),
		trackerMarkers: mergeSpansById(initial.trackerMarkers, historical.trackerMarkers),
		bgCheckMarkers: mergeByTime(initial.bgCheckMarkers, historical.bgCheckMarkers),

		// Merge spans (keyed by id in {#each} blocks — must dedup by id)
		pumpModeSpans: mergeSpansById(initial.pumpModeSpans, historical.pumpModeSpans),
		profileSpans: mergeSpansById(initial.profileSpans, historical.profileSpans),
		overrideSpans: mergeSpansById(initial.overrideSpans, historical.overrideSpans),
		activitySpans: mergeSpansById(initial.activitySpans, historical.activitySpans),
		tempBasalSpans: mergeSpansById(initial.tempBasalSpans, historical.tempBasalSpans),
		basalDeliverySpans: mergeSpansById(
			initial.basalDeliverySpans,
			historical.basalDeliverySpans
		),

		// Take the max values from either dataset
		maxIob: Math.max(initial.maxIob ?? 0, historical.maxIob ?? 0),
		maxCob: Math.max(initial.maxCob ?? 0, historical.maxCob ?? 0),
		maxBasalRate: Math.max(initial.maxBasalRate ?? 0, historical.maxBasalRate ?? 0),
	};
}
