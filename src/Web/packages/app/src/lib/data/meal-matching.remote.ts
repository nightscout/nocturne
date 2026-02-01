/**
 * Remote functions for meal matching operations
 */
import { getRequestEvent, query, command } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';
import { getNotifications } from './notifications.remote';

/**
 * Get a food entry for review
 */
export const getFoodEntry = query(
	z.object({
		id: z.string(),
	}),
	async ({ id }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			return await apiClient.mealMatching.getFoodEntry(id);
		} catch (err) {
			console.error('Error loading food entry:', err);
			throw error(500, 'Failed to load food entry');
		}
	}
);

/**
 * Accept a meal match with optional carb adjustment and time offset
 */
export const acceptMatch = command(
	z.object({
		foodEntryId: z.string(),
		treatmentId: z.string(),
		carbs: z.number(),
		timeOffsetMinutes: z.number(),
	}),
	async ({ foodEntryId, treatmentId, carbs, timeOffsetMinutes }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			await apiClient.mealMatching.acceptMatch({
				foodEntryId,
				treatmentId,
				carbs,
				timeOffsetMinutes,
			});
			await getNotifications().refresh();
			return { success: true };
		} catch (err: unknown) {
			console.error('Error accepting meal match:', err);
			// Try to extract error message from NSwag exception
			const message = err instanceof Error ? err.message : 'Failed to accept meal match';
			console.error('Error message:', message);
			throw error(500, message);
		}
	}
);

/**
 * Dismiss a meal match
 */
export const dismissMatch = command(
	z.object({
		foodEntryId: z.string(),
	}),
	async ({ foodEntryId }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			await apiClient.mealMatching.dismissMatch({ foodEntryId });
			await getNotifications().refresh();
			return { success: true };
		} catch (err) {
			console.error('Error dismissing meal match:', err);
			throw error(500, 'Failed to dismiss meal match');
		}
	}
);

/**
 * Get suggested meal matches for a date range
 */
export const getSuggestions = query(
	z.object({
		from: z.string(),
		to: z.string(),
	}),
	async ({ from, to }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			// Convert string dates to Date objects for the NSwag client
			const fromDate = new Date(from);
			const toDate = new Date(to);
			// Set toDate to end of day
			toDate.setHours(23, 59, 59, 999);
			return await apiClient.mealMatching.getSuggestions(fromDate, toDate);
		} catch (err) {
			console.error('Error loading meal suggestions:', err);
			throw error(500, 'Failed to load meal suggestions');
		}
	}
);
