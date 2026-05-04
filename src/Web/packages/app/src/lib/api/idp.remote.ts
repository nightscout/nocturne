/**
 * Remote functions for the Insulin Dosing Profile (IDP) report Fetches sensor
 * glucose, boluses, carb intakes, insulin delivery stats, profile summary,
 * extended glucose analytics, averaged stats, and basal analysis
 */
import { z } from "zod";
import { getRequestEvent, query } from "$app/server";
import { error } from "@sveltejs/kit";
import { DiabetesPopulation } from "$lib/api";

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

  const pageSize = 1000;

  // Fetch sensor glucose readings
  const glucoseResult = await apiClient.sensorGlucose.getAll(
    startDate,
    endDate,
    10000
  );
  const entries = glucoseResult.data ?? [];

  // Paginate boluses
  let allBoluses: Awaited<ReturnType<typeof apiClient.bolus.getAll>>["data"] =
    [];
  let offset = 0;
  let hasMore = true;

  while (hasMore) {
    const batch = await apiClient.bolus.getAll(
      startDate,
      endDate,
      pageSize,
      offset
    );
    allBoluses = allBoluses!.concat(batch.data ?? []);

    if ((batch.data?.length ?? 0) < pageSize) {
      hasMore = false;
    } else {
      offset += pageSize;
    }

    if (offset >= 50000) {
      console.warn("Bolus fetch reached safety limit of 50,000 records");
      hasMore = false;
    }
  }

  // Paginate carb intakes
  let allCarbIntakes: Awaited<
    ReturnType<typeof apiClient.nutrition.getCarbIntakes>
  >["data"] = [];
  offset = 0;
  hasMore = true;

  while (hasMore) {
    const batch = await apiClient.nutrition.getCarbIntakes(
      startDate,
      endDate,
      pageSize,
      offset
    );
    allCarbIntakes = allCarbIntakes!.concat(batch.data ?? []);

    if ((batch.data?.length ?? 0) < pageSize) {
      hasMore = false;
    } else {
      offset += pageSize;
    }

    if (offset >= 50000) {
      console.warn("CarbIntake fetch reached safety limit of 50,000 records");
      hasMore = false;
    }
  }

  const boluses = allBoluses!;
  const carbIntakes = allCarbIntakes!;
  const population = DiabetesPopulation.Type1Adult; // TODO: Get from user settings

  // Fetch insulin delivery stats, profile summary, extended glucose analytics,
  // averaged stats, and basal analysis in parallel
  const [
    insulinDeliveryStats,
    profileSummary,
    analysis,
    averagedStats,
    basalAnalysis,
    aidSystemMetrics,
  ] = await Promise.all([
    apiClient.statistics.getInsulinDeliveryStatistics(startDate, endDate),
    apiClient.profile.getProfileSummary(startDate, endDate),
    apiClient.statistics.analyzeGlucoseDataExtended({
      entries,
      boluses,
      carbIntakes,
      population,
    }),
    apiClient.statistics.calculateAveragedStats(entries),
    apiClient.statistics.getBasalAnalysis(startDate, endDate),
    apiClient.statistics.getAidSystemMetrics(startDate, endDate),
  ]);

  return {
    entries,
    boluses,
    carbIntakes,
    insulinDeliveryStats,
    profileSummary,
    analysis,
    averagedStats,
    basalAnalysis,
    aidSystemMetrics,
    dateRange: {
      from: startDate.toISOString(),
      to: endDate.toISOString(),
      lastUpdated: new Date().toISOString(),
    },
  };
});
