/**
 * Remote functions for statistics calculations
 */
import { getRequestEvent, query } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';
import type { Entry, AveragedStats } from '$lib/api';

const calculateAveragedStatsSchema = z.object({
	entries: z.array(z.object({
		sgv: z.number().optional(),
		mgdl: z.number().optional(),
		date: z.union([z.string(), z.date()]).optional(),
		mills: z.number().optional(),
	})),
});

/**
 * Calculate averaged stats from entries
 */
export const calculateAveragedStats = query(calculateAveragedStatsSchema, async ({ entries }) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		// Transform to Entry[] for the API
		const stats: AveragedStats[] = await apiClient.statistics.calculateAveragedStats(entries as Entry[]);
		return stats;
	} catch (err) {
		console.error('Error calculating averaged stats:', err);
		throw error(500, 'Failed to calculate statistics');
	}
});
