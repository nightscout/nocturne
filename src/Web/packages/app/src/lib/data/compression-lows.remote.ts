/**
 * Remote functions for compression low detection and review
 */
import { getRequestEvent, query, command } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';
import { CompressionLowStatus } from '$lib/api';

/**
 * Get compression low suggestions with optional filtering
 */
export const getCompressionLowSuggestions = query(
	z.object({
		status: z.enum(['Pending', 'Accepted', 'Dismissed']).optional(),
		nightOf: z.string().optional()
	}),
	async ({ status, nightOf }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const statusEnum = status ? CompressionLowStatus[status] : undefined;
			return await apiClient.compressionLow.getSuggestions(statusEnum, nightOf);
		} catch (err) {
			console.error('Error loading compression low suggestions:', err);
			throw error(500, 'Failed to load compression low suggestions');
		}
	}
);

/**
 * Get a single suggestion with glucose entries for charting
 */
export const getCompressionLowSuggestion = query(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.compressionLow.getSuggestion(id);
	} catch (err) {
		console.error('Error loading compression low suggestion:', err);
		throw error(500, 'Failed to load compression low suggestion');
	}
});

/**
 * Accept a compression low suggestion with adjusted bounds
 */
export const acceptCompressionLow = command(
	z.object({
		id: z.string(),
		startMills: z.number(),
		endMills: z.number()
	}),
	async ({ id, startMills, endMills }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const stateSpan = await apiClient.compressionLow.acceptSuggestion(id, {
				startMills,
				endMills
			});
			return stateSpan;
		} catch (err) {
			console.error('Error accepting compression low:', err);
			throw error(500, 'Failed to accept compression low');
		}
	}
);

/**
 * Dismiss a compression low suggestion
 */
export const dismissCompressionLow = command(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		await apiClient.compressionLow.dismissSuggestion(id);
		return { success: true };
	} catch (err) {
		console.error('Error dismissing compression low:', err);
		throw error(500, 'Failed to dismiss compression low');
	}
});

/**
 * Delete a compression low suggestion and its associated state span
 */
export const deleteCompressionLow = command(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		await apiClient.compressionLow.deleteSuggestion(id);
		return { success: true };
	} catch (err) {
		console.error('Error deleting compression low:', err);
		throw error(500, 'Failed to delete compression low');
	}
});

/**
 * Manually trigger detection for a single night or date range
 * Use nightOf for a single night, or startDate/endDate for a range (max 90 days)
 */
export const triggerCompressionLowDetection = command(
	z.object({
		nightOf: z.string().optional(),
		startDate: z.string().optional(),
		endDate: z.string().optional()
	}),
	async ({ nightOf, startDate, endDate }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			return await apiClient.compressionLow.triggerDetection({ nightOf, startDate, endDate });
		} catch (err) {
			console.error('Error triggering compression low detection:', err);
			throw error(500, 'Failed to trigger compression low detection');
		}
	}
);
