/**
 * Remote functions for reports data
 * Provides entries, treatments, and analysis data for all report pages
 */
import { z } from 'zod';
import { DiabetesPopulationSchema } from '$lib/api/generated/schemas';
import { getRequestEvent, query } from '$app/server';
import { error } from '@sveltejs/kit';
import { DiabetesPopulation, type BasalPoint } from '$lib/api';

/**
 * Input schema for date range queries.
 * Uses nullish() to accept both null and undefined, matching the date-params hook
 * which uses nullable defaults for runed compatibility.
 */
const DateRangeSchema = z.object({
	days: z.number().nullish(),
	from: z.string().nullish(),
	to: z.string().nullish(),
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

		const entriesQuery = JSON.stringify({
			date: {
				$gte: startDate.toISOString(),
				$lte: endDate.toISOString(),
			},
		});
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

		const treatmentsQuery = JSON.stringify({
			created_at: {
				$gte: startDate.toISOString(),
				$lte: endDate.toISOString(),
			},
		});
		const pageSize = 1000;

		// Fetch all treatments by paginating through results using v4 endpoint
		let allTreatments: Awaited<ReturnType<typeof apiClient.treatments.getTreatments>> = [];
		let offset = 0;
		let hasMore = true;

		while (hasMore) {
			const batch = await apiClient.treatments.getTreatments(undefined, pageSize, offset, treatmentsQuery);
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
		population: DiabetesPopulationSchema.optional(),
	}),
	async ({ entries, treatments, population = DiabetesPopulation.Type1Adult }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		return apiClient.statistics.analyzeGlucoseDataExtended({
			entries,
			treatments,
			population: population as DiabetesPopulation,
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

		const entriesQuery = JSON.stringify({
			date: {
				$gte: startDate.toISOString(),
				$lte: endDate.toISOString(),
			},
		});
		const treatmentsQuery = JSON.stringify({
			created_at: {
				$gte: startDate.toISOString(),
				$lte: endDate.toISOString(),
			},
		});
		console.log(treatmentsQuery)
		// Fetch entries first
		const entries = await apiClient.entries.getEntries2(entriesQuery);

		// Paginate treatments using v4 endpoint
		const pageSize = 1000;
		let allTreatments: Awaited<ReturnType<typeof apiClient.treatments.getTreatments>> = [];
		let offset = 0;
		let hasMore = true;

		while (hasMore) {
			const batch = await apiClient.treatments.getTreatments(undefined, pageSize, offset, treatmentsQuery);
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
		const population = DiabetesPopulation.Type1Adult; // TODO: Get from user settings

		// Get summary, analysis, averaged stats, and basal data in parallel
		const [summary, analysis, averagedStats, chartData] = await Promise.all([
			apiClient.statistics.getMultiPeriodStatistics(),
			apiClient.statistics.analyzeGlucoseDataExtended({
				entries,
				treatments,
				population,
			}),
			apiClient.statistics.calculateAveragedStats(entries),
			apiClient.chartData.getDashboardChartData(
				startDate.getTime(),
				endDate.getTime(),
				5 // 5-minute intervals
			),
		]);

		return {
			entries,
			treatments,
			summary,
			analysis,
			averagedStats,
			basalSeries: chartData.basalSeries ?? [] as BasalPoint[],
			dateRange: {
				from: startDate.toISOString(),
				to: endDate.toISOString(),
				lastUpdated: new Date().toISOString(),
			},
		};
	}
);

/**
 * Input schema for site change impact analysis.
 * Uses nullish() for date fields to match date-params hook.
 */
const SiteChangeImpactSchema = z.object({
	days: z.number().nullish(),
	from: z.string().nullish(),
	to: z.string().nullish(),
	hoursBeforeChange: z.number().optional().default(12),
	hoursAfterChange: z.number().optional().default(24),
	bucketSizeMinutes: z.number().optional().default(30),
});

export type SiteChangeImpactInput = z.infer<typeof SiteChangeImpactSchema>;

/**
 * Get site change impact analysis
 * Analyzes glucose patterns around pump site changes
 */
export const getSiteChangeImpact = query(
	SiteChangeImpactSchema.optional(),
	async (input) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;
		const { startDate, endDate } = calculateDateRange(input);

		const entriesQuery = JSON.stringify({
			date: {
				$gte: startDate.toISOString(),
				$lte: endDate.toISOString(),
			},
		});
		const treatmentsQuery = JSON.stringify({
			created_at: {
				$gte: startDate.toISOString(),
				$lte: endDate.toISOString(),
			},
		});

		// Fetch entries
		const entries = await apiClient.entries.getEntries2(entriesQuery);

		// Paginate treatments to get all site changes using v4 endpoint
		const pageSize = 1000;
		let allTreatments: Awaited<ReturnType<typeof apiClient.treatments.getTreatments>> = [];
		let offset = 0;
		let hasMore = true;

		while (hasMore) {
			const batch = await apiClient.treatments.getTreatments(undefined, pageSize, offset, treatmentsQuery);
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

		// Call the site change impact analysis endpoint
		const analysis = await apiClient.statistics.calculateSiteChangeImpact({
			entries,
			treatments: allTreatments,
			hoursBeforeChange: input?.hoursBeforeChange ?? 12,
			hoursAfterChange: input?.hoursAfterChange ?? 24,
			bucketSizeMinutes: input?.bucketSizeMinutes ?? 30,
		});

		return {
			analysis,
			dateRange: {
				from: startDate.toISOString(),
				to: endDate.toISOString(),
			},
		};
	}
);
