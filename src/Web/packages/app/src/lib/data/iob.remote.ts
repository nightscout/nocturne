/**
 * Remote functions for IOB (Insulin on Board) data
 */
import { getRequestEvent, query } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';

const hourlyIobSchema = z.object({
	intervalMinutes: z.number().optional().default(5),
	hours: z.number().optional().default(24),
	startTime: z.number().optional(),
});

/**
 * Get hourly IOB data for charting
 */
export const getHourlyIob = query(hourlyIobSchema, async (props) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const response = await apiClient.iob.getHourlyIob(
			props.intervalMinutes,
			props.hours,
			props.startTime
		);

		// Transform API response to match chart data format
		return {
			data: response.data?.map((item) => ({
				timeSlot: item.timeSlot || 0,
				hour: item.hour || 0,
				minute: item.minute || 0,
				timeLabel: item.timeLabel || '',
				totalIOB: item.totalIOB || 0,
				bolusIOB: item.bolusIOB || 0,
				basalIOB: item.basalIOB || 0,
			})) || [],
		};
	} catch (err) {
		console.error('Error loading IOB data:', err);
		throw error(500, 'Failed to load IOB data');
	}
});
