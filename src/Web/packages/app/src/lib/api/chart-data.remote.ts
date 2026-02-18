/**
 * Remote function for chart data with server-side calculations.
 * All categorization, color mapping, and treatment classification is done server-side.
 * Single call replaces 7+ separate API calls.
 */
import { getRequestEvent, query } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';
import { transformChartData } from '$lib/utils/chart-data-transform';

const chartDataSchema = z.object({
	startTime: z.number(),
	endTime: z.number(),
	intervalMinutes: z.number().optional().default(5),
});

export const getChartData = query(chartDataSchema, async ({ startTime, endTime, intervalMinutes }) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const data = await apiClient.chartData.getDashboardChartData(
			startTime,
			endTime,
			intervalMinutes
		);

		return transformChartData(data);
	} catch (err) {
		console.error('Error loading chart data:', err);
		throw error(500, 'Failed to load chart data');
	}
});
