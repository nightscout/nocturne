/**
 * Remote functions for reports data Provides sensor glucose, boluses, carb
 * intakes, device events, and analysis data for all report pages
 */
import { z } from "zod";
import { DiabetesPopulationSchema } from "$lib/api/generated/schemas";
import { getRequestEvent, query } from "$app/server";
import { error } from "@sveltejs/kit";
import { DiabetesPopulation, ClusterConfidence } from "$lib/api";
import { fetchAllGlucose } from "./glucose-pagination";

/**
 * Input schema for date range queries. Uses nullish() to accept both null and
 * undefined, matching the date-params hook which uses nullable defaults for
 * runed compatibility.
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

/** Get sensor glucose readings for a date range */
export const getEntries = query(DateRangeSchema.optional(), async (input) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;
  const { startDate, endDate } = calculateDateRange(input);

  const entries = await fetchAllGlucose(apiClient, startDate, endDate);

  return {
    entries,
    dateRange: {
      from: startDate.toISOString(),
      to: endDate.toISOString(),
    },
  };
});

/** Get boluses and carb intakes for a date range with pagination support */
export const getBolusesAndCarbs = query(
  DateRangeSchema.optional(),
  async (input) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;
    const { startDate, endDate } = calculateDateRange(input);

    const pageSize = 1000;

    // Fetch all boluses by paginating through results
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

      // Safety limit to prevent infinite loops
      if (offset >= 50000) {
        console.warn("Bolus fetch reached safety limit of 50,000 records");
        hasMore = false;
      }
    }

    // Fetch all carb intakes by paginating through results
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

/** Get glucose analysis for entries, boluses, and carb intakes */
export const getAnalysis = query(
  z.object({
    entries: z.array(z.any()),
    boluses: z.array(z.any()),
    carbIntakes: z.array(z.any()),
    population: DiabetesPopulationSchema.optional(),
  }),
  async ({
    entries,
    boluses,
    carbIntakes,
    population = DiabetesPopulation.Type1Adult,
  }) => {
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
 * Reports data for pages that render raw readings (overview, executive summary, AGP,
 * week-to-week): sensor glucose for the range plus server-computed extended analytics
 * and averaged stats.
 */
export const getReportsData = query(
  DateRangeSchema.optional(),
  async (input) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;
    const { startDate, endDate } = calculateDateRange(input);

    const [entries, { analysis, averagedStats }] = await Promise.all([
      fetchAllGlucose(apiClient, startDate, endDate),
      apiClient.statistics.getRangeAnalytics(startDate, endDate),
    ]);

    return {
      entries,
      analysis,
      averagedStats,
      dateRange: {
        from: startDate.toISOString(),
        to: endDate.toISOString(),
        lastUpdated: new Date().toISOString(),
      },
    };
  }
);

/**
 * Server-side extended analytics and averaged stats for a date range. Used by report
 * pages that render only computed metrics (comparison, glucose distribution).
 */
export const getReportsAnalysis = query(
  DateRangeSchema.optional(),
  async (input) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;
    const { startDate, endDate } = calculateDateRange(input);

    const { analysis, averagedStats } =
      await apiClient.statistics.getRangeAnalytics(startDate, endDate);

    return {
      analysis,
      averagedStats,
      dateRange: {
        from: startDate.toISOString(),
        to: endDate.toISOString(),
      },
    };
  }
);

/**
 * Boluses and basal series for a date range, for the basal-analysis and
 * insulin-delivery reports.
 */
export const getBasalReportData = query(
  DateRangeSchema.optional(),
  async (input) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;
    const { startDate, endDate } = calculateDateRange(input);

    const [bolusResult, basalSeries] = await Promise.all([
      apiClient.bolus.getAll(startDate, endDate, 10000),
      apiClient.chartData.getBasalSeries(startDate.getTime(), endDate.getTime()),
    ]);

    return {
      boluses: bolusResult.data ?? [],
      basalSeries: basalSeries ?? [],
      dateRange: {
        from: startDate.toISOString(),
        to: endDate.toISOString(),
      },
    };
  }
);

/**
 * Sensor data-quality / integrity report for a date range: the raw glucose trace plus the
 * server-computed sensor-integrity analysis (noise clusters + cluster-linked hypo events).
 * The frontend renders this verbatim — all detection and scoring happens backend-side.
 */
export const getDataQualityReport = query(
  DateRangeSchema.optional(),
  async (input) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;
    const { startDate, endDate } = calculateDateRange(input);

    const [entries, integrity] = await Promise.all([
      fetchAllGlucose(apiClient, startDate, endDate),
      apiClient.sensorIntegrity.analyze(
        startDate,
        endDate,
        undefined,
        false,
        ClusterConfidence.Medium,
        false,
        70,
        3
      ),
    ]);

    return {
      entries,
      integrity,
      dateRange: {
        from: startDate.toISOString(),
        to: endDate.toISOString(),
        lastUpdated: new Date().toISOString(),
      },
    };
  }
);

/**
 * Input schema for site change impact analysis. Uses nullish() for date fields
 * to match date-params hook.
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
 * Get site change impact analysis Analyzes glucose patterns around pump site
 * changes
 */
export const getSiteChangeImpact = query(
  SiteChangeImpactSchema.optional(),
  async (input) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;
    const { startDate, endDate } = calculateDateRange(input);

    // Fetch all sensor glucose readings
    const entries = await fetchAllGlucose(apiClient, startDate, endDate);

    // Paginate device events to get all site changes
    const pageSize = 1000;
    let allDeviceEvents: Awaited<
      ReturnType<typeof apiClient.deviceEvent.getAll>
    >["data"] = [];
    let offset = 0;
    let hasMore = true;

    while (hasMore) {
      const batch = await apiClient.deviceEvent.getAll(
        startDate,
        endDate,
        pageSize,
        offset
      );
      allDeviceEvents = allDeviceEvents!.concat(batch.data ?? []);

      if ((batch.data?.length ?? 0) < pageSize) {
        hasMore = false;
      } else {
        offset += pageSize;
      }

      if (offset >= 50000) {
        console.warn(
          "DeviceEvent fetch reached safety limit of 50,000 records"
        );
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
