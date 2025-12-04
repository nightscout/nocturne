/**
 * Remote functions for compatibility/discrepancy analysis
 */
import { z } from 'zod';
import { query } from '$app/server';
import { error } from '@sveltejs/kit';
import { getApiClient } from '$lib/server/api';
import type { ResponseMatchType } from '$lib/api/generated/nocturne-api-client';

const CompatibilityFiltersSchema = z.object({
	requestPath: z.string().optional(),
	overallMatch: z.number().optional(),
	requestMethod: z.string().optional(),
	count: z.number().optional(),
	skip: z.number().optional(),
});

/**
 * Get compatibility dashboard data (config, metrics, endpoints, analyses)
 */
export const getCompatibilityData = query(
	CompatibilityFiltersSchema.optional(),
	async (filters) => {
		const client = getApiClient();

		try {
			const requestPath = filters?.requestPath || undefined;
			const overallMatch = filters?.overallMatch as ResponseMatchType | undefined;
			const requestMethod = filters?.requestMethod || undefined;
			const count = filters?.count ?? 100;
			const skip = filters?.skip ?? 0;

			const [config, metrics, endpoints, analysesData] = await Promise.all([
				client.compatibility.getConfiguration(),
				client.compatibility.getMetrics(undefined, undefined),
				client.compatibility.getEndpointMetrics(undefined, undefined),
				client.compatibility.getAnalyses(
					requestPath,
					overallMatch,
					requestMethod,
					undefined,
					undefined,
					count,
					skip
				),
			]);

			return {
				config,
				metrics,
				endpoints,
				analyses: analysesData.analyses || [],
				total: analysesData.total || 0,
				filters: {
					requestPath: filters?.requestPath || '',
					overallMatch: filters?.overallMatch?.toString() || '',
					requestMethod: filters?.requestMethod || '',
					count,
					skip,
				},
			};
		} catch (err) {
			console.error('Error loading compatibility data:', err);
			if ((err as any).status) {
				throw err;
			}
			throw error(500, 'Failed to load compatibility data');
		}
	}
);

/**
 * Get a single analysis detail by ID
 */
export const getAnalysisDetail = query(z.string(), async (analysisId) => {
	const client = getApiClient();

	try {
		const analysis = await client.compatibility.getAnalysisDetail(analysisId);
		return { analysis };
	} catch (err) {
		console.error('Error loading analysis detail:', err);
		if ((err as any).status === 404) {
			throw error(404, 'Analysis not found');
		}
		if ((err as any).status) {
			throw err;
		}
		throw error(500, 'Failed to load analysis detail');
	}
});
