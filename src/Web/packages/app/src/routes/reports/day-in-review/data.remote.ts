/**
 * Remote functions for day-in-review report
 * Fetches entries and treatments for a specific day
 */
import { z } from 'zod';
import { query } from '$app/server';
import { error } from '@sveltejs/kit';
import { getApiClient } from '$lib/server/api';

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

		const apiClient = getApiClient();

		// Set date boundaries
		const dayStart = new Date(date);
		dayStart.setHours(0, 0, 0, 0);
		const dayEnd = new Date(date);
		dayEnd.setHours(23, 59, 59, 999);

		// Fetch entries and treatments for this specific day
		const entriesQuery = `find[date][$gte]=${dayStart.toISOString()}&find[date][$lte]=${dayEnd.toISOString()}`;
		const treatmentsQuery = `find[created_at][$gte]=${dayStart.toISOString()}&find[created_at][$lte]=${dayEnd.toISOString()}`;

		const [entries, treatments] = await Promise.all([
			apiClient.entries.getEntries2(entriesQuery).catch(() => []),
			apiClient.treatments.getTreatments2(treatmentsQuery).catch(() => []),
		]);

		return {
			date: dateParam,
			entries,
			treatments,
			dateRange: {
				from: dayStart.toISOString(),
				to: dayEnd.toISOString(),
			},
		};
	}
);
