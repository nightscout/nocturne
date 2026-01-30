/**
 * Remote functions for chart data with server-side calculations
 * Provides pre-computed IOB, COB, and basal time series for dashboard charts
 */
import { getRequestEvent, query } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';
import { type BasalPoint } from '$lib/api';

/**
 * Input schema for chart data queries
 */
const chartDataSchema = z.object({
	startTime: z.number(),
	endTime: z.number(),
	intervalMinutes: z.number().optional().default(5),
});

export type ChartDataInput = z.infer<typeof chartDataSchema>;

/**
 * Time series point with timestamp and value
 */
export interface TimeSeriesPoint {
	time: Date;
	value: number;
}

/**
 * Dashboard chart data response
 */
export interface DashboardChartData {
	iobSeries: TimeSeriesPoint[];
	cobSeries: TimeSeriesPoint[];
	basalSeries: BasalPoint[];
	defaultBasalRate: number;
	maxBasalRate: number;
	maxIob: number;
	maxCob: number;
}

/**
 * Get dashboard chart data with pre-calculated IOB, COB, and basal series
 * All calculations are performed server-side for performance
 */
export const getChartData = query(chartDataSchema, async ({ startTime, endTime, intervalMinutes }) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const data = await apiClient.chartData.getDashboardChartData(
			startTime,
			endTime,
			intervalMinutes
		);

		// Transform API response to frontend-friendly format with Date objects
		return {
			iobSeries: data.iobSeries?.map((p) => ({
				time: new Date(p.timestamp ?? 0),
				value: p.value ?? 0,
			})) ?? [],
			cobSeries: data.cobSeries?.map((p) => ({
				time: new Date(p.timestamp ?? 0),
				value: p.value ?? 0,
			})) ?? [],
			basalSeries: data.basalSeries ?? [],
			defaultBasalRate: data.defaultBasalRate ?? 1.0,
			maxBasalRate: data.maxBasalRate ?? 3.0,
			maxIob: data.maxIob ?? 5.0,
			maxCob: data.maxCob ?? 100.0,
		} satisfies DashboardChartData;
	} catch (err) {
		console.error('Error loading chart data:', err);
		throw error(500, 'Failed to load chart data');
	}
});
