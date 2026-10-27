import type { CarbMarkerDto } from '#lib/api/generated/nocturne-api-client.js';
import type { TransformedChartData } from '#lib/utils/chart-data-transform.js';

// Either half may have seen the pump last, or neither.
const latestOf = (a: number | null, b: number | null): number | null =>
	a == null ? b : b == null ? a : Math.max(a, b);

// An empty id is a missing one: rows keyed by it would collide on "".
const hasId = (id: unknown): id is string => id != null && id !== '';

/**
 * The key of a row in a keyed `{#each}`: its record id, or its time and position when
 * it has none, since rows without an id are never deduped against each other.
 */
export function markerKey(id: string | null | undefined, time: Date, index: number): string {
	return hasId(id) ? id : `${time.getTime()}-${index}`;
}

/**
 * The identity of a carb marker, or undefined when it has no record id. An offset
 * marker shares its meal's treatmentId, so its time tells it apart from the meal.
 */
export function carbMarkerKey(
	marker: Pick<CarbMarkerDto, 'treatmentId' | 'isOffset'> & { time: Date }
): string | undefined {
	if (!hasId(marker.treatmentId)) return undefined;
	return marker.isOffset ? `${marker.treatmentId}@${marker.time.getTime()}` : marker.treatmentId;
}

/**
 * Merge historical chart data into initial chart data.
 * Historical data contains older records that should be prepended to arrays.
 */
export function mergeChartData(
	initial: TransformedChartData,
	historical: TransformedChartData | null
): TransformedChartData {
	if (!historical) return initial;

	// Helper to merge arrays by time, avoiding duplicates. A row with an id is deduped by
	// that id alone, since a treatment moved across the boundary between the two fetches
	// appears in both; time dedup applies only to rows without one.
	const timeValue = (value: unknown) => (value instanceof Date ? value.getTime() : value);
	const mergeByTime = <T,>(
		initialArr: T[],
		historicalArr: T[],
		timeOf: (item: T) => unknown,
		idOf?: (item: T) => unknown
	): T[] => {
		if (!initialArr || !historicalArr) return initialArr || historicalArr || [];
		const initialTimes = new Set(initialArr.map((item) => timeValue(timeOf(item))));
		const initialIds = new Set(
			idOf ? initialArr.map((item) => idOf(item)).filter(hasId) : []
		);
		const uniqueHistorical = historicalArr.filter((item) => {
			const id = idOf?.(item);
			if (hasId(id)) return !initialIds.has(id);
			return !initialTimes.has(timeValue(timeOf(item)));
		});
		return [...uniqueHistorical, ...initialArr];
	};

	// Helper to merge span arrays by id. Spans without an id are all kept.
	// Spans that straddle the initial/historical boundary can appear in both
	// datasets with the same id but different startTime, so time-based dedup
	// alone would let duplicates through and cause Svelte each_key_duplicate.
	const mergeSpansById = <T extends { id?: unknown }>(
		initialArr: T[],
		historicalArr: T[]
	): T[] => {
		if (!initialArr || !historicalArr) return initialArr || historicalArr || [];
		const seenIds = new Set(initialArr.map((item) => item.id).filter(hasId));
		const uniqueHistorical = historicalArr.filter((item) => {
			if (hasId(item.id)) {
				if (seenIds.has(item.id)) return false;
				seenIds.add(item.id);
				return true;
			}
			return true;
		});
		return [...uniqueHistorical, ...initialArr];
	};

	// An offset marker comes with its meal, so a meal present in the initial window
	// makes the historical copy of its offsets stale.
	const mergeCarbMarkers = (
		initialArr: TransformedChartData['carbMarkers'],
		historicalArr: TransformedChartData['carbMarkers']
	) => {
		const initialMeals = new Set(
			initialArr.filter((m) => !m.isOffset).map((m) => m.treatmentId).filter(hasId)
		);
		return mergeByTime(
			initialArr,
			historicalArr.filter((m) => !(m.isOffset && hasId(m.treatmentId) && initialMeals.has(m.treatmentId))),
			(p) => p.time,
			carbMarkerKey
		);
	};

	// Every field is listed explicitly rather than spreading `initial`. A spread
	// satisfies the return type on its own, so a collection left out of the merge
	// keeps only the initial window's rows instead of failing to compile.
	return {
		// Time series
		iobSeries: mergeByTime(initial.iobSeries, historical.iobSeries, (p) => p.time),
		cobSeries: mergeByTime(initial.cobSeries, historical.cobSeries, (p) => p.time),
		basalSeries: mergeByTime(initial.basalSeries, historical.basalSeries, (p) => p.timestamp),
		glucoseData: mergeByTime(initial.glucoseData, historical.glucoseData, (p) => p.time),

		// Markers drop a repeat by id, or by time when they have no id
		bolusMarkers: mergeByTime(initial.bolusMarkers, historical.bolusMarkers, (p) => p.time, (p) => p.treatmentId),
		carbMarkers: mergeCarbMarkers(initial.carbMarkers, historical.carbMarkers),
		deviceEventMarkers: mergeByTime(
			initial.deviceEventMarkers,
			historical.deviceEventMarkers,
			(p) => p.time,
			(p) => p.treatmentId
		),
		bgCheckMarkers: mergeByTime(initial.bgCheckMarkers, historical.bgCheckMarkers, (p) => p.time, (p) => p.treatmentId),

		// Merge markers and spans keyed by id in {#each} blocks — must dedup by id
		systemEventMarkers: mergeSpansById(initial.systemEventMarkers, historical.systemEventMarkers),
		trackerMarkers: mergeSpansById(initial.trackerMarkers, historical.trackerMarkers),
		basalInjectionMarkers: mergeSpansById(
			initial.basalInjectionMarkers,
			historical.basalInjectionMarkers
		),
		pumpModeSpans: mergeSpansById(initial.pumpModeSpans, historical.pumpModeSpans),
		profileSpans: mergeSpansById(initial.profileSpans, historical.profileSpans),
		overrideSpans: mergeSpansById(initial.overrideSpans, historical.overrideSpans),
		activitySpans: mergeSpansById(initial.activitySpans, historical.activitySpans),
		tempBasalSpans: mergeSpansById(initial.tempBasalSpans, historical.tempBasalSpans),
		basalDeliverySpans: mergeSpansById(
			initial.basalDeliverySpans,
			historical.basalDeliverySpans
		),
		lastPumpSyncMills: latestOf(initial.lastPumpSyncMills, historical.lastPumpSyncMills),

		// Thresholds are profile-derived and identical across both halves, except
		// glucoseYMax, which the server sizes to the max SGV it was asked for — the
		// streamed half can carry a higher excursion, and the chart's yDomain clips
		// anything above it.
		defaultBasalRate: initial.defaultBasalRate,
		thresholds: {
			...initial.thresholds,
			glucoseYMax: Math.max(
				initial.thresholds.glucoseYMax,
				historical.thresholds.glucoseYMax
			),
		},

		// Take the max values from either dataset
		maxIob: Math.max(initial.maxIob ?? 0, historical.maxIob ?? 0),
		minIob: Math.min(initial.minIob ?? 0, historical.minIob ?? 0),
		maxCob: Math.max(initial.maxCob ?? 0, historical.maxCob ?? 0),
		maxBasalRate: Math.max(initial.maxBasalRate ?? 0, historical.maxBasalRate ?? 0),
	};
}

/**
 * Swap the window starting at `startTime` for a fresh fetch of it, leaving older
 * rows alone. Unlike `mergeChartData`, rows inside the window that the server no
 * longer returns (a deleted or moved treatment) do not survive. Markers keyed by id
 * also drop an older copy of an id the fresh set holds, so a treatment moved from
 * before `startTime` into the window is not drawn twice. One moved out of the window
 * disappears until the next full load: fresh rows before `startTime` are ignored.
 */
export function replaceWindow(
	current: TransformedChartData,
	recent: TransformedChartData,
	startTime: number
): TransformedChartData {
	const ms = (value: unknown) => (value instanceof Date ? value.getTime() : Number(value));
	const swap = <T,>(
		older: T[],
		fresh: T[],
		timeOf: (item: T) => unknown,
		idOf?: (item: T) => unknown
	): T[] => {
		const keptFresh = fresh
			.filter((item) => ms(timeOf(item)) >= startTime)
			.sort((a, b) => ms(timeOf(a)) - ms(timeOf(b)));
		const freshIds = new Set(idOf ? keptFresh.map((item) => idOf(item)).filter(hasId) : []);
		return [
			...older.filter((item) => {
				if (ms(timeOf(item)) >= startTime) return false;
				const id = idOf?.(item);
				return !(hasId(id) && freshIds.has(id));
			}),
			...keptFresh,
		];
	};
	// An offset marker sits on its meal's side of the window, because the server sends it
	// only with its meal, so it is kept or replaced along with that meal whatever its own time.
	const swapCarbMarkers = (
		older: TransformedChartData['carbMarkers'],
		fresh: TransformedChartData['carbMarkers']
	) => {
		const anchorOf = (rows: typeof older) => {
			const mealTimes = new Map(
				rows.filter((m) => !m.isOffset && hasId(m.treatmentId)).map((m) => [m.treatmentId, ms(m.time)])
			);
			return (m: (typeof rows)[number]) =>
				(m.isOffset ? mealTimes.get(m.treatmentId) : undefined) ?? ms(m.time);
		};
		const freshAnchor = anchorOf(fresh);
		const keptFresh = fresh
			.filter((m) => freshAnchor(m) >= startTime)
			.sort((a, b) => ms(a.time) - ms(b.time));
		const freshMeals = new Set(
			keptFresh.filter((m) => !m.isOffset).map((m) => m.treatmentId).filter(hasId)
		);
		const olderAnchor = anchorOf(older);
		return [
			...older.filter((m) => olderAnchor(m) < startTime && !(hasId(m.treatmentId) && freshMeals.has(m.treatmentId))),
			...keptFresh,
		];
	};
	// A span that began before the window and is still running comes back in the
	// fresh fetch under the same id, so ids in the fresh set replace the old row too.
	const swapSpans = <T extends { id?: unknown; startTime: Date }>(
		older: T[],
		fresh: T[]
	): T[] => {
		const freshIds = new Set(fresh.map((s) => s.id).filter(hasId));
		return [
			...older.filter(
				(s) => s.startTime.getTime() < startTime && !(hasId(s.id) && freshIds.has(s.id))
			),
			...[...fresh].sort((a, b) => a.startTime.getTime() - b.startTime.getTime()),
		];
	};
	const peak = (floor: number, values: Iterable<number | undefined>) =>
		Math.max(floor, ...Array.from(values, (v) => v ?? 0));
	const trough = (ceiling: number, values: Iterable<number | undefined>) =>
		Array.from(values, (v) => v ?? 0).reduce((low, v) => Math.min(low, v), ceiling);

	const glucoseData = swap(current.glucoseData, recent.glucoseData, (p) => p.time);
	// Mirrors the server's sizing: 20 over the highest reading, between 300 and 400.
	const olderPeak = peak(0, glucoseData.map((p) => p.sgv));
	const glucoseYMax = Math.max(
		recent.thresholds.glucoseYMax,
		Math.min(400, Math.max(280, olderPeak) + 20)
	);
	const iobSeries = swap(current.iobSeries, recent.iobSeries, (p) => p.time);
	const cobSeries = swap(current.cobSeries, recent.cobSeries, (p) => p.time);
	const basalSeries = swap(current.basalSeries, recent.basalSeries, (p) => p.timestamp);

	return {
		iobSeries,
		cobSeries,
		basalSeries,
		glucoseData,
		bolusMarkers: swap(current.bolusMarkers, recent.bolusMarkers, (p) => p.time, (p) => p.treatmentId),
		carbMarkers: swapCarbMarkers(current.carbMarkers, recent.carbMarkers),
		deviceEventMarkers: swap(
			current.deviceEventMarkers,
			recent.deviceEventMarkers,
			(p) => p.time,
			(p) => p.treatmentId
		),
		bgCheckMarkers: swap(current.bgCheckMarkers, recent.bgCheckMarkers, (p) => p.time, (p) => p.treatmentId),
		systemEventMarkers: swap(
			current.systemEventMarkers,
			recent.systemEventMarkers,
			(p) => p.time,
			(p) => p.id
		),
		trackerMarkers: swap(current.trackerMarkers, recent.trackerMarkers, (p) => p.time, (p) => p.id),
		basalInjectionMarkers: swap(
			current.basalInjectionMarkers,
			recent.basalInjectionMarkers,
			(p) => p.time,
			(p) => p.id
		),
		pumpModeSpans: swapSpans(current.pumpModeSpans, recent.pumpModeSpans),
		profileSpans: swapSpans(current.profileSpans, recent.profileSpans),
		overrideSpans: swapSpans(current.overrideSpans, recent.overrideSpans),
		activitySpans: swapSpans(current.activitySpans, recent.activitySpans),
		tempBasalSpans: swapSpans(current.tempBasalSpans, recent.tempBasalSpans),
		basalDeliverySpans: swapSpans(current.basalDeliverySpans, recent.basalDeliverySpans),
		lastPumpSyncMills: latestOf(current.lastPumpSyncMills, recent.lastPumpSyncMills),
		defaultBasalRate: current.defaultBasalRate,
		thresholds: { ...current.thresholds, glucoseYMax },
		maxIob: peak(recent.maxIob ?? 0, iobSeries.map((p) => p.value)),
		minIob: trough(recent.minIob ?? 0, iobSeries.map((p) => p.value)),
		maxCob: peak(recent.maxCob ?? 0, cobSeries.map((p) => p.value)),
		maxBasalRate: peak(recent.maxBasalRate ?? 0, basalSeries.map((p) => p.rate ?? undefined)),
	};
}
