/**
 * Remote functions for the Insulin Dosing Profile (IDP) report. Fetches the sensor
 * reading count, the hourly insulin delivery pattern, insulin delivery stats, the profiles in force,
 * extended glucose analytics, averaged stats, and basal analysis
 */
import { getRequestEvent, query } from "$app/server";
import { DateRangeSchema, resolveReportRange } from "./report-range";
import { readable } from "#lib/server/patient-timezone.js";

export type { DateRangeInput } from "./report-range";

/** Combined query to get all data needed for the Insulin Dosing Profile report. */
export const getIdpData = query(DateRangeSchema.optional(), async (input) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;
  const { startDate, endDate } = await resolveReportRange(input, 14);

  // The page shows how many readings the window holds, not the readings, so one row and the
  // total stand in for them. Every read is independent of the others.
  const [readings, hourlyDelivery, insulinDeliveryStats, activeSchedules, rangeAnalytics, aidSystemMetrics] =
    await Promise.all([
      apiClient.sensorGlucose.getAll(startDate, endDate, 1, 0, undefined, undefined, undefined, true),
      apiClient.statistics.getHourlyInsulinDelivery(startDate, endDate),
      apiClient.statistics.getInsulinDeliveryStatistics(startDate, endDate),
      // Therapy settings need therapy.read; without it the report renders
      // with no basal schedule rather than not at all.
      readable(() => apiClient.profile.getActiveSchedules(startDate, endDate)),
      apiClient.statistics.getRangeAnalytics(startDate, endDate),
      apiClient.statistics.getAidSystemMetrics(startDate, endDate),
    ]);
  return {
    readingCount: readings.pagination?.total ?? 0,
    hourlyBolusCounts: hourlyDelivery.hours ?? [],
    hourlyTimeZoneUnavailableReason: hourlyDelivery.timeZoneUnavailableReason,
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
