/**
 * Remote functions for reports data
 * Provides sensor glucose, boluses, carb intakes, device events, and analysis data for all report pages
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
		// Default to last 7 days
		endDate = new Date();
		startDate = new Date(endDate);
		startDate.setDate(endDate.getDate() - 6);
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
 * Get sensor glucose readings for a date range
 */
export const getEntries = query(
	DateRangeSchema.optional(),
	async (input) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;
		const { startDate, endDate } = calculateDateRange(input);

		const result = await apiClient.glucose.getSensorGlucose(startDate.getTime(), endDate.getTime(), 10000);
		const entries = result.data ?? [];

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
 * Get boluses and carb intakes for a date range with pagination support
 */
export const getBolusesAndCarbs = query(
	DateRangeSchema.optional(),
	async (input) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;
		const { startDate, endDate } = calculateDateRange(input);

		const fromMs = startDate.getTime();
		const toMs = endDate.getTime();
		const pageSize = 1000;

		// Fetch all boluses by paginating through results
		let allBoluses: Awaited<ReturnType<typeof apiClient.insulin.getBoluses>>['data'] = [];
		let offset = 0;
		let hasMore = true;

		while (hasMore) {
			const batch = await apiClient.insulin.getBoluses(fromMs, toMs, pageSize, offset);
			allBoluses = allBoluses!.concat(batch.data ?? []);

			if ((batch.data?.length ?? 0) < pageSize) {
				hasMore = false;
			} else {
				offset += pageSize;
			}

			// Safety limit to prevent infinite loops
			if (offset >= 50000) {
				console.warn('Bolus fetch reached safety limit of 50,000 records');
				hasMore = false;
			}
		}

		// Fetch all carb intakes by paginating through results
		let allCarbIntakes: Awaited<ReturnType<typeof apiClient.nutrition.getCarbIntakes>>['data'] = [];
		offset = 0;
		hasMore = true;

		while (hasMore) {
			const batch = await apiClient.nutrition.getCarbIntakes(fromMs, toMs, pageSize, offset);
			allCarbIntakes = allCarbIntakes!.concat(batch.data ?? []);

			if ((batch.data?.length ?? 0) < pageSize) {
				hasMore = false;
			} else {
				offset += pageSize;
			}

			if (offset >= 50000) {
				console.warn('CarbIntake fetch reached safety limit of 50,000 records');
				hasMore = false;
			}
		}

		return {
			boluses: allBoluses!,
			carbIntakes: allCarbIntakes!,
			dateRange: {
				from: startDate.toISOString(),
				to: endDate.toISOString(),
			},
		};
	}
);

/**
 * Get glucose analysis for entries, boluses, and carb intakes
 */
export const getAnalysis = query(
	z.object({
		entries: z.array(z.any()),
		boluses: z.array(z.any()),
		carbIntakes: z.array(z.any()),
		population: DiabetesPopulationSchema.optional(),
	}),
	async ({ entries, boluses, carbIntakes, population = DiabetesPopulation.Type1Adult }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		return apiClient.statistics.analyzeGlucoseDataExtended({
			entries,
			boluses,
			carbIntakes,
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

		const fromMs = startDate.getTime();
		const toMs = endDate.getTime();
		const pageSize = 1000;

		console.log(JSON.stringify({ from: new Date(fromMs).toISOString(), to: new Date(toMs).toISOString() }))
		// Fetch sensor glucose readings
		const glucoseResult = await apiClient.glucose.getSensorGlucose(fromMs, toMs, 10000);
		const entries = glucoseResult.data ?? [];

		// Paginate boluses
		let allBoluses: Awaited<ReturnType<typeof apiClient.insulin.getBoluses>>['data'] = [];
		let offset = 0;
		let hasMore = true;

		while (hasMore) {
			const batch = await apiClient.insulin.getBoluses(fromMs, toMs, pageSize, offset);
			allBoluses = allBoluses!.concat(batch.data ?? []);

			if ((batch.data?.length ?? 0) < pageSize) {
				hasMore = false;
			} else {
				offset += pageSize;
			}

			if (offset >= 50000) {
				console.warn('Bolus fetch reached safety limit of 50,000 records');
				hasMore = false;
			}
		}

		// Paginate carb intakes
		let allCarbIntakes: Awaited<ReturnType<typeof apiClient.nutrition.getCarbIntakes>>['data'] = [];
		offset = 0;
		hasMore = true;

		while (hasMore) {
			const batch = await apiClient.nutrition.getCarbIntakes(fromMs, toMs, pageSize, offset);
			allCarbIntakes = allCarbIntakes!.concat(batch.data ?? []);

			if ((batch.data?.length ?? 0) < pageSize) {
				hasMore = false;
			} else {
				offset += pageSize;
			}

			if (offset >= 50000) {
				console.warn('CarbIntake fetch reached safety limit of 50,000 records');
				hasMore = false;
			}
		}

		const boluses = allBoluses!;
		const carbIntakes = allCarbIntakes!;
		const population = DiabetesPopulation.Type1Adult; // TODO: Get from user settings

		// Get summary, analysis, averaged stats, and basal data in parallel
		const [summary, analysis, averagedStats, chartData] = await Promise.all([
			apiClient.statistics.getMultiPeriodStatistics(),
			apiClient.statistics.analyzeGlucoseDataExtended({
				entries,
				boluses,
				carbIntakes,
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
			boluses,
			carbIntakes,
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

		const fromMs = startDate.getTime();
		const toMs = endDate.getTime();

		// Fetch sensor glucose readings
		const glucoseResult = await apiClient.glucose.getSensorGlucose(fromMs, toMs, 10000);
		const entries = glucoseResult.data ?? [];

		// Paginate device events to get all site changes
		const pageSize = 1000;
		let allDeviceEvents: Awaited<ReturnType<typeof apiClient.observations.getDeviceEvents>>['data'] = [];
		let offset = 0;
		let hasMore = true;

		while (hasMore) {
			const batch = await apiClient.observations.getDeviceEvents(fromMs, toMs, pageSize, offset);
			allDeviceEvents = allDeviceEvents!.concat(batch.data ?? []);

			if ((batch.data?.length ?? 0) < pageSize) {
				hasMore = false;
			} else {
				offset += pageSize;
			}

			if (offset >= 50000) {
				console.warn('DeviceEvent fetch reached safety limit of 50,000 records');
				hasMore = false;
			}
		}

		// Call the site change impact analysis endpoint
		const analysis = await apiClient.statistics.calculateSiteChangeImpact({
			entries,
			deviceEvents: allDeviceEvents!,
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
