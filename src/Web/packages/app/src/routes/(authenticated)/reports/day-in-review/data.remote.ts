/**
 * Remote functions for day-in-review report
 * Fetches sensor glucose, boluses, and carb intakes for a specific day
 */
import { z } from 'zod';
import { getRequestEvent, query } from '$app/server';
import { error } from '@sveltejs/kit';
import { getAll as getApsSnapshots } from '$api/generated/apsSnapshots.generated.remote';
import { getInsulinDeliveryStatistics } from '$api/generated/statistics.generated.remote';
import { getPatientTimeZone } from '$api/report-range';
import { getLocalDayBoundariesUtc } from '$lib/utils/timezone';

/**
 * Get day-in-review data for a specific date
 */
export const getDayInReviewData = query(
	z.string(), // date string in ISO format (YYYY-MM-DD)
	async (dateParam) => {
		if (!dateParam) {
			throw error(400, 'Date parameter is required');
		}

		const date = new Date(dateParam);
		if (isNaN(date.getTime())) {
			throw error(400, 'Invalid date parameter');
		}

		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		// Resolve the patient's timezone so the day boundaries are their day, not the server's
		const timezone = await getPatientTimeZone();
		const { start: dayStart, end: dayEnd } = getLocalDayBoundariesUtc(dateParam, timezone);

		// Fetch v4 data + APS snapshots for historical predictions
		const [entriesResponse, bolusResponse, carbResponse, apsResponse] = await Promise.all([
			apiClient.sensorGlucose.getAll(dayStart, dayEnd, 10000),
			apiClient.bolus.getAll(dayStart, dayEnd, 1000),
			apiClient.nutrition.getCarbIntakes(dayStart, dayEnd, 1000),
			getApsSnapshots({ from: dayStart.getTime(), to: dayEnd.getTime(), limit: 1000, sort: 'timestamp_asc' }),
		]);

		const entries = entriesResponse.data ?? [];
		const boluses = bolusResponse.data ?? [];
		const carbIntakes = carbResponse.data ?? [];
		const apsSnapshots = apsResponse.data ?? [];

		// Calculate analysis from the backend - this includes treatmentSummary
		const analysis = entries.length > 0
			? await apiClient.statistics.analyzeGlucoseDataExtended({
					entries,
					boluses,
					carbIntakes,
					population: 0 as const, // Type1Adult
				})
			: null;

		// Use the treatmentSummary from analysis (if available) to avoid redundant API call
		// The backend AnalyzeGlucoseDataExtended already calculates TreatmentSummary
		// If no entries but we have boluses/carbIntakes, calculate treatmentSummary directly
		const treatmentSummary = analysis?.treatmentSummary
			?? ((boluses.length > 0 || carbIntakes.length > 0)
				? await apiClient.statistics.calculateTreatmentSummary({ boluses, carbIntakes })
				: null);

		// Fetch insulin delivery stats (includes scheduled vs additional basal breakdown)
		const insulinDelivery = await getInsulinDeliveryStatistics({
			startDate: dayStart,
			endDate: dayEnd,
		});

		return {
			date: dateParam,
			entries,
			boluses,
			carbIntakes,
			analysis,
			treatmentSummary,
			insulinDelivery,
			apsSnapshots,
			dateRange: {
				from: dayStart.toISOString(),
				to: dayEnd.toISOString(),
			},
		};
	}
);

