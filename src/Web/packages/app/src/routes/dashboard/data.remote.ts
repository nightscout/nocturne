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

		// Fetch all dashboard data in parallel using the API client
		const [metrics, endpoints, analyses, status] = await Promise.all([
			apiClient.discrepancy.getCompatibilityMetrics(fromDate, toDate),
			apiClient.discrepancy.getEndpointMetrics(fromDate, toDate),
			apiClient.discrepancy.getDiscrepancyAnalyses(undefined, undefined, undefined, undefined, 20, 0),
			apiClient.discrepancy.getCompatibilityStatus(),
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
		const requestPath = filters?.requestPath || undefined;
		const overallMatch = filters?.overallMatch;
		const fromDate = filters?.fromDate ? new Date(filters.fromDate) : undefined;
		const toDate = filters?.toDate ? new Date(filters.toDate) : undefined;
		const count = filters?.count ?? 50;
		const skip = filters?.skip ?? 0;

		const analyses = await apiClient.discrepancy.getDiscrepancyAnalyses(
			requestPath,
			overallMatch,
			fromDate,
			toDate,
			count,
			skip
		);

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
		const analysis = await apiClient.discrepancy.getDiscrepancyAnalysis(id);
		return { analysis };
	} catch (err) {
		console.error('Error loading analysis:', err);
		if ((err as any).status === 404) {
			throw error(404, 'Analysis not found');
		}
		throw error(500, 'Failed to load analysis');
	}
});
