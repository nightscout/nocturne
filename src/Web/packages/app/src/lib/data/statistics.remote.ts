/**
 * Remote functions for statistics calculations
 */
import { getRequestEvent, query } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';
import type { Entry, AveragedStats, TimeInRangeMetrics, MultiPeriodStatistics, InsulinDeliveryStatistics, DailyBasalBolusRatioResponse, BasalAnalysisResponse } from '$lib/api';

const calculateAveragedStatsSchema = z.object({
	entries: z.array(z.object({
		sgv: z.number().optional(),
		mgdl: z.number().optional(),
		date: z.union([z.string(), z.date()]).optional(),
		mills: z.number().optional(),
	})),
});

const calculateTimeInRangeSchema = z.object({
	entries: z.array(z.object({
		sgv: z.number().optional(),
		mgdl: z.number().optional(),
		date: z.union([z.string(), z.date()]).optional(),
		mills: z.number().optional(),
	})),
	config: z.object({
		severeLow: z.number(),
		low: z.number(),
		target: z.number(),
		high: z.number(),
		severeHigh: z.number(),
	}).optional(),
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

/**
 * Calculate time in range metrics from entries
 */
export const calculateTimeInRange = query(calculateTimeInRangeSchema, async ({ entries, config }) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const metrics: TimeInRangeMetrics = await apiClient.statistics.calculateTimeInRange({
			entries: entries as Entry[],
			...(config && { config }),
		});
		return metrics;
	} catch (err) {
		console.error('Error calculating time in range:', err);
		throw error(500, 'Failed to calculate time in range');
	}
});

/**
 * Get multi-period statistics (1, 3, 7, 30, 90 days)
 * Includes TIR, treatment summaries with TDD breakdown, and analytics
 */
export const getMultiPeriodStatistics = query(z.object({}), async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const stats: MultiPeriodStatistics = await apiClient.statistics.getMultiPeriodStatistics();
		return stats;
	} catch (err) {
		console.error('Error fetching multi-period statistics:', err);
		throw error(500, 'Failed to fetch statistics');
	}
});

const getInsulinDeliveryStatsSchema = z.object({
	startDate: z.string(), // ISO date string
	endDate: z.string(), // ISO date string
});

/**
 * Get comprehensive insulin delivery statistics for a date range
 * Backend fetches treatments from the database and calculates stats
 * Includes TDD, basal/bolus breakdown, I:C ratio, meal vs correction boluses, etc.
 */
export const getInsulinDeliveryStats = query(
	getInsulinDeliveryStatsSchema,
	async ({ startDate, endDate }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const stats: InsulinDeliveryStatistics = await apiClient.statistics.getInsulinDeliveryStatistics(
				new Date(startDate),
				new Date(endDate)
			);
			return stats;
		} catch (err) {
			console.error('Error fetching insulin delivery stats:', err);
			throw error(500, 'Failed to fetch insulin delivery statistics');
		}
	}
);

const getDailyBasalBolusRatiosSchema = z.object({
	startDate: z.string(), // ISO date string
	endDate: z.string(), // ISO date string
});

/**
 * Get daily basal/bolus ratio statistics for a date range
 * Backend fetches treatments from the database and calculates daily breakdown
 * Includes daily basal/bolus amounts, percentages, and period averages
 */
export const getDailyBasalBolusRatios = query(
	getDailyBasalBolusRatiosSchema,
	async ({ startDate, endDate }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const stats: DailyBasalBolusRatioResponse = await apiClient.statistics.getDailyBasalBolusRatios(
				new Date(startDate),
				new Date(endDate)
			);
			return stats;
		} catch (err) {
			console.error('Error fetching daily basal/bolus ratios:', err);
			throw error(500, 'Failed to fetch daily basal/bolus ratios');
		}
	}
);

const getBasalAnalysisSchema = z.object({
	startDate: z.string(), // ISO date string
	endDate: z.string(), // ISO date string
});

/**
 * Get comprehensive basal analysis statistics for a date range
 * Backend fetches treatments from the database and calculates stats
 * Includes basic stats, temp basal info, and hourly percentiles for charts
 */
export const getBasalAnalysis = query(
	getBasalAnalysisSchema,
	async ({ startDate, endDate }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const stats: BasalAnalysisResponse = await apiClient.statistics.getBasalAnalysis(
				new Date(startDate),
				new Date(endDate)
			);
			return stats;
		} catch (err) {
			console.error('Error fetching basal analysis:', err);
			throw error(500, 'Failed to fetch basal analysis');
		}
	}
);
