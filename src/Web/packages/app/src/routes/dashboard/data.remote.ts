/**
 * Remote functions for dashboard data
 */
import { z } from 'zod';
import { getRequestEvent, query } from '$app/server';
import { error } from '@sveltejs/kit';


const DashboardFiltersSchema = z.object({
	fromDate: z.string().optional(),
	toDate: z.string().optional(),
});

/**
 * Get dashboard compatibility metrics and data
 */
export const getDashboardData = query(DashboardFiltersSchema.optional(), async (filters) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const fromDate = filters?.fromDate ? new Date(filters.fromDate) : undefined;
		const toDate = filters?.toDate ? new Date(filters.toDate) : undefined;

		// Fetch all dashboard data in parallel
		const [metricsResponse, endpointResponse, analysesResponse, statusResponse] =
			await Promise.all([
				fetch(
					`${apiClient.baseUrl}/api/v3/discrepancy/metrics?${new URLSearchParams({
						...(fromDate && { fromDate: fromDate.toISOString() }),
						...(toDate && { toDate: toDate.toISOString() }),
					})}`
				),
				fetch(
					`${apiClient.baseUrl}/api/v3/discrepancy/endpoints?${new URLSearchParams({
						...(fromDate && { fromDate: fromDate.toISOString() }),
						...(toDate && { toDate: toDate.toISOString() }),
					})}`
				),
				fetch(`${apiClient.baseUrl}/api/v3/discrepancy/analyses?count=20&skip=0`),
				fetch(`${apiClient.baseUrl}/api/v3/discrepancy/status`),
			]);

		if (!metricsResponse.ok) {
			throw error(500, 'Failed to fetch compatibility metrics');
		}
		if (!endpointResponse.ok) {
			throw error(500, 'Failed to fetch endpoint metrics');
		}
		if (!analysesResponse.ok) {
			throw error(500, 'Failed to fetch recent analyses');
		}
		if (!statusResponse.ok) {
			throw error(500, 'Failed to fetch compatibility status');
		}

		const [metrics, endpoints, analyses, status] = await Promise.all([
			metricsResponse.json(),
			endpointResponse.json(),
			analysesResponse.json(),
			statusResponse.json(),
		]);

		return {
			metrics,
			endpoints,
			analyses,
			status,
			filters: {
				fromDate: fromDate?.toISOString(),
				toDate: toDate?.toISOString(),
			},
		};
	} catch (err) {
		console.error('Error loading dashboard data:', err);
		throw error(500, 'Failed to load dashboard data');
	}
});

const AnalysesFiltersSchema = z.object({
	requestPath: z.string().optional(),
	overallMatch: z.number().optional(),
	fromDate: z.string().optional(),
	toDate: z.string().optional(),
	count: z.number().optional(),
	skip: z.number().optional(),
});

/**
 * Get analyses list with filtering
 */
export const getAnalyses = query(AnalysesFiltersSchema.optional(), async (filters) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const requestPath = filters?.requestPath || '';
		const overallMatch = filters?.overallMatch;
		const fromDate = filters?.fromDate ? new Date(filters.fromDate) : undefined;
		const toDate = filters?.toDate ? new Date(filters.toDate) : undefined;
		const count = filters?.count ?? 50;
		const skip = filters?.skip ?? 0;

		const queryParams = new URLSearchParams({
			count: count.toString(),
			skip: skip.toString(),
			...(requestPath && { requestPath }),
			...(overallMatch !== undefined && { overallMatch: overallMatch.toString() }),
			...(fromDate && { fromDate: fromDate.toISOString() }),
			...(toDate && { toDate: toDate.toISOString() }),
		});

		const response = await fetch(`${apiClient.baseUrl}/api/v3/discrepancy/analyses?${queryParams}`);

		if (!response.ok) {
			throw error(500, 'Failed to fetch discrepancy analyses');
		}

		const analyses = await response.json();

		return {
			analyses,
			filters: {
				requestPath,
				overallMatch,
				fromDate: fromDate?.toISOString(),
				toDate: toDate?.toISOString(),
				count,
				skip,
			},
		};
	} catch (err) {
		console.error('Error loading analyses:', err);
		throw error(500, 'Failed to load analyses');
	}
});

/**
 * Get a single analysis detail by ID
 */
export const getAnalysisById = query(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const response = await fetch(`${apiClient.baseUrl}/api/v3/discrepancy/analyses/${id}`);

		if (!response.ok) {
			if (response.status === 404) {
				throw error(404, 'Analysis not found');
			}
			throw error(500, 'Failed to fetch analysis');
		}

		const analysis = await response.json();

		return { analysis };
	} catch (err) {
		console.error('Error loading analysis:', err);
		if ((err as any).status) {
			throw err;
		}
		throw error(500, 'Failed to load analysis');
	}
});
