/**
 * Remote functions for compatibility/discrepancy analysis
 */
import { z } from 'zod';
import { getRequestEvent, query } from '$app/server';
import { error } from '@sveltejs/kit';
import { ResponseMatchType } from '$lib/api/generated/nocturne-api-client';
import { errorStatus } from '$lib/forms/submit-error';

const CompatibilityFiltersSchema = z.object({
	requestPath: z.string().optional(),
	overallMatch: z.enum(ResponseMatchType).optional().catch(undefined),
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
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const requestPath = filters?.requestPath;
			const overallMatch = filters?.overallMatch;
			const requestMethod = filters?.requestMethod;
			const count = filters?.count ?? 100;
			const skip = filters?.skip ?? 0;

			const [config, metrics, endpoints, analysesData] = await Promise.all([
				apiClient.compatibility.getConfiguration(),
				apiClient.compatibility.getMetrics(),
				apiClient.compatibility.getEndpointMetrics(),
				apiClient.compatibility.getAnalyses(
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
			if (errorStatus(err)) {
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

		const { locals } = getRequestEvent();
		const { apiClient } = locals;

	try {
		const analysis = await apiClient.compatibility.getAnalysisDetail(analysisId);
		return { analysis };
	} catch (err) {
		console.error('Error loading analysis detail:', err);
		if (errorStatus(err) === 404) {
			throw error(404, 'Analysis not found');
		}
		if (errorStatus(err)) {
			throw err;
		}
		throw error(500, 'Failed to load analysis detail');
	}
});

/**
 * Get compatibility metrics only (for polling)
 */
export const getCompatibilityMetrics = query(async () => {

	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.compatibility.getMetrics(undefined, undefined);
	} catch (err) {
		console.error('Error loading compatibility metrics:', err);
		throw error(500, 'Failed to load compatibility metrics');
	}
});

/**
 * Get analyses list only (for polling)
 */
export const getCompatibilityAnalyses = query(
	CompatibilityFiltersSchema.optional(),
	async (filters) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;
		try {
			const requestPath = filters?.requestPath;
			const overallMatch = filters?.overallMatch;
			const requestMethod = filters?.requestMethod;
			const count = filters?.count ?? 100;
			const skip = filters?.skip ?? 0;

			const analysesData = await apiClient.compatibility.getAnalyses(
				requestPath,
				overallMatch,
				requestMethod,
				undefined,
				undefined,
				count,
				skip
			);

			return analysesData.analyses || [];
		} catch (err) {
			console.error('Error loading compatibility analyses:', err);
			throw error(500, 'Failed to load compatibility analyses');
		}
	}
);
