/**
 * Remote functions for reports data
 * Provides entries, treatments, and analysis data for all report pages
 */
import { z } from 'zod';
import { getRequestEvent, query } from '$app/server';
import { error } from '@sveltejs/kit';

/**
 * Input schema for date range queries
 */
const DateRangeSchema = z.object({
	days: z.number().optional(),
	from: z.string().optional(),
	to: z.string().optional(),
});

export type DateRangeInput = z.infer<typeof DateRangeSchema>;

/**
 * Calculate date range from input parameters
 */
function calculateDateRange(input?: DateRangeInput): { startDate: Date; endDate: Date } {
	let startDate: Date;
	let endDate: Date;

	if (input?.from && input?.to) {
		startDate = new Date(input.from);
		endDate = new Date(input.to);
	} else if (input?.days) {
		endDate = new Date();
		startDate = new Date(endDate);
		startDate.setDate(endDate.getDate() - (input.days - 1));
	} else {
		// Default to last 24 hours
		endDate = new Date();
		startDate = new Date(endDate);
		startDate.setDate(endDate.getDate() - 1);
	}

	// Validate dates
	if (isNaN(startDate.getTime()) || isNaN(endDate.getTime())) {
		throw error(400, 'Invalid date parameters provided');
	}

	// Set to full day boundaries
	startDate.setHours(0, 0, 0, 0);
	endDate.setHours(23, 59, 59, 999);

	return { startDate, endDate };
}

/**
 * Get entries for a date range
 */
export const getEntries = query(
	DateRangeSchema.optional(),
	async (input) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;
		const { startDate, endDate } = calculateDateRange(input);

		const entriesQuery = `find[date][$gte]=${startDate.toISOString()}&find[date][$lte]=${endDate.toISOString()}`;
		const entries = await apiClient.entries.getEntries2(entriesQuery);

		return {
			entries,
			dateRange: {
				from: startDate.toISOString(),
				to: endDate.toISOString(),
			},
		};
	}
);

/**
 * Get treatments for a date range with pagination support
 */
export const getTreatments = query(
	DateRangeSchema.optional(),
	async (input) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;
		const { startDate, endDate } = calculateDateRange(input);

		const treatmentsQuery = `find[created_at][$gte]=${startDate.toISOString()}&find[created_at][$lte]=${endDate.toISOString()}`;
		const pageSize = 1000;

		// Fetch all treatments by paginating through results
		let allTreatments: Awaited<ReturnType<typeof apiClient.treatments.getTreatments2>> = [];
		let offset = 0;
		let hasMore = true;

		while (hasMore) {
			const batch = await apiClient.treatments.getTreatments2(treatmentsQuery, pageSize, offset);
			allTreatments = allTreatments.concat(batch);

			if (batch.length < pageSize) {
				hasMore = false;
			} else {
				offset += pageSize;
			}

			// Safety limit to prevent infinite loops
			if (offset >= 50000) {
				console.warn('Treatment fetch reached safety limit of 50,000 records');
				hasMore = false;
			}
		}

		return {
			treatments: allTreatments,
			dateRange: {
				from: startDate.toISOString(),
				to: endDate.toISOString(),
			},
		};
	}
);

/**
 * Get glucose analysis for entries and treatments
 */
export const getAnalysis = query(
	z.object({
		entries: z.array(z.any()),
		treatments: z.array(z.any()),
		population: z.union([z.literal(0), z.literal(1), z.literal(2), z.literal(3), z.literal(4), z.literal(5)]).optional(),
	}),
	async ({ entries, treatments, population = 0 }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		return apiClient.statistics.analyzeGlucoseDataExtended({
			entries,
			treatments,
			population: population as 0 | 1 | 2 | 3 | 4 | 5,
		});
	}
);

/**
 * Get multi-period statistics summary
 */
export const getSummary = query(async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;
	return apiClient.statistics.getMultiPeriodStatistics();
});

/**
 * Combined query to get all reports data in one call
 * This is the main entry point for reports pages
 */
export const getReportsData = query(
	DateRangeSchema.optional(),
	async (input) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;
		const { startDate, endDate } = calculateDateRange(input);

		const entriesQuery = `find[date][$gte]=${startDate.toISOString()}&find[date][$lte]=${endDate.toISOString()}`;
		const treatmentsQuery = `find[created_at][$gte]=${startDate.toISOString()}&find[created_at][$lte]=${endDate.toISOString()}`;

		// Fetch entries first
		const entries = await apiClient.entries.getEntries2(entriesQuery);

		// Paginate treatments
		const pageSize = 1000;
		let allTreatments: Awaited<ReturnType<typeof apiClient.treatments.getTreatments2>> = [];
		let offset = 0;
		let hasMore = true;

		while (hasMore) {
			const batch = await apiClient.treatments.getTreatments2(treatmentsQuery, pageSize, offset);
			allTreatments = allTreatments.concat(batch);

			if (batch.length < pageSize) {
				hasMore = false;
			} else {
				offset += pageSize;
			}

			if (offset >= 50000) {
				console.warn('Treatment fetch reached safety limit of 50,000 records');
				hasMore = false;
			}
		}

		const treatments = allTreatments;

		// Get summary and analysis
		const [summary, analysis] = await Promise.all([
			apiClient.statistics.getMultiPeriodStatistics(),
			apiClient.statistics.analyzeGlucoseDataExtended({
				entries,
				treatments,
				population: 0 as const,
			}),
		]);

		return {
			entries,
			treatments,
			summary,
			analysis,
			dateRange: {
				from: startDate.toISOString(),
				to: endDate.toISOString(),
				lastUpdated: new Date().toISOString(),
			},
		};
	}
);
