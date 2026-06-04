/**
 * Remote functions for the Insulin Dosing Profile (IDP) report Fetches sensor
 * glucose, boluses, carb intakes, insulin delivery stats, profile summary,
 * extended glucose analytics, averaged stats, and basal analysis
 */
import { z } from "zod";
import { getRequestEvent, query } from "$app/server";
import { error } from "@sveltejs/kit";
import { fetchAllGlucose } from "./glucose-pagination";

/**
 * Input schema for date range queries. Uses nullish() to accept both null and
 * undefined, matching the date-params hook.
 */
const DateRangeSchema = z.object({
  days: z.number().nullish(),
  from: z.string().nullish(),
  to: z.string().nullish(),
});

export type DateRangeInput = z.infer<typeof DateRangeSchema>;

/** Calculate date range from input parameters */
function calculateDateRange(input?: DateRangeInput): {
  startDate: Date;
  endDate: Date;
} {
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
    throw error(400, "Invalid date parameters provided");
  }

  // Set to full day boundaries
  startDate.setHours(0, 0, 0, 0);
  endDate.setHours(23, 59, 59, 999);

  return { startDate, endDate };
}

/**
 * Combined query to get all data needed for the Insulin Dosing Profile report.
 * Fetches entries, boluses, and carb intakes first (with pagination), then
 * fetches analytics and profile data in parallel.
 */
export const getIdpData = query(DateRangeSchema.optional(), async (input) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;
  const { startDate, endDate } = calculateDateRange(input);

  // Raw readings (paginated) and boluses for the charts.
  const [entries, bolusResult] = await Promise.all([
    fetchAllGlucose(apiClient, startDate, endDate),
    apiClient.bolus.getAll(startDate, endDate, 10000),
  ]);
  const boluses = bolusResult.data ?? [];

  // Insulin/profile/AID stats and server-side glucose analytics in parallel.
  const [insulinDeliveryStats, profileSummary, rangeAnalytics, aidSystemMetrics] =
    await Promise.all([
      apiClient.statistics.getInsulinDeliveryStatistics(startDate, endDate),
      apiClient.profile.getProfileSummary(startDate, endDate),
      apiClient.statistics.getRangeAnalytics(startDate, endDate),
      apiClient.statistics.getAidSystemMetrics(startDate, endDate),
    ]);

  return {
    entries,
    boluses,
    insulinDeliveryStats,
    profileSummary,
    analysis: rangeAnalytics.analysis,
    averagedStats: rangeAnalytics.averagedStats,
    aidSystemMetrics,
    dateRange: {
      from: startDate.toISOString(),
      to: endDate.toISOString(),
      lastUpdated: new Date().toISOString(),
    },
  };
});
