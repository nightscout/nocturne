/**
 * Remote functions for the Insulin Dosing Profile (IDP) report Fetches sensor
 * glucose, boluses, carb intakes, insulin delivery stats, the profiles in force,
 * extended glucose analytics, averaged stats, and basal analysis
 */
import { getRequestEvent, query } from "$app/server";
import { fetchAllGlucose } from "./glucose-pagination";
import { DateRangeSchema, resolveReportRange } from "./report-range";
import { readable } from "$lib/server/patient-timezone";

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
  const [insulinDeliveryStats, activeSchedules, rangeAnalytics, aidSystemMetrics] =
    await Promise.all([
      apiClient.statistics.getInsulinDeliveryStatistics(startDate, endDate),
      // Therapy settings need therapy.read; without it the report renders
      // with no basal schedule rather than not at all.
      readable(() => apiClient.profile.getActiveSchedules(startDate, endDate)),
      apiClient.statistics.getRangeAnalytics(startDate, endDate),
      apiClient.statistics.getAidSystemMetrics(startDate, endDate),
    ]);

  return {
    entries,
    boluses,
    insulinDeliveryStats,
    activeSchedules: activeSchedules ?? [],
    analysis: rangeAnalytics.analysis,
    averagedStats: rangeAnalytics.averagedStats,
    timeZoneUnavailableReason: rangeAnalytics.timeZoneUnavailableReason,
    aidSystemMetrics,
    dateRange: {
      from: startDate,
      to: endDate,
      lastUpdated: new Date().toISOString(),
    },
  };
});
