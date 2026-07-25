/**
 * Remote functions for the Insulin Dosing Profile (IDP) report Fetches sensor
 * glucose, boluses, carb intakes, insulin delivery stats, profile summary,
 * extended glucose analytics, averaged stats, and basal analysis
 */
import { getRequestEvent, query } from "$app/server";
import { fetchAllGlucose } from "./glucose-pagination";
import { DateRangeSchema, resolveReportRange } from "./report-range";

export type { DateRangeInput } from "./report-range";

/**
 * Combined query to get all data needed for the Insulin Dosing Profile report.
 * Fetches entries, boluses, and carb intakes first (with pagination), then
 * fetches analytics and profile data in parallel.
 */
export const getIdpData = query(DateRangeSchema.optional(), async (input) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;
  const { startDate, endDate } = await resolveReportRange(input, 14);

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
