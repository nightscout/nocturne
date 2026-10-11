/**
 * Remote functions for reports data Provides sensor glucose, boluses, carb
 * intakes, device events, and analysis data for all report pages
 */
import { z } from "zod";
import { getRequestEvent, query } from "$app/server";
import { DiabetesPopulation, ClusterConfidence } from "$lib/api";
import { fetchAllGlucose } from "./glucose-pagination";
import { DateRangeSchema, resolveReportRange } from "./report-range";
import { readable } from "$lib/server/patient-timezone";
import { compare } from "$api/generated/cgmComparisons.generated.remote";

export type { DateRangeInput } from "./report-range";

/**
 * Who a printed report is about. Both fields are null when the record leaves
 * them unset or the viewer (a public share) may not read the patient record.
 */
export const getReportSubject = query(async () => {
  const { apiClient } = getRequestEvent().locals;
  const record = await readable(() => apiClient.patientRecord.getPatientRecord());
  return {
    name: record?.preferredName?.trim() || null,
    dateOfBirth: record?.dateOfBirth ?? null,
  };
});

/** Get sensor glucose readings for a date range */
export const getEntries = query(DateRangeSchema.optional(), async (input) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;
  const { startDate, endDate } = await resolveReportRange(input);

  const entries = await fetchAllGlucose(apiClient, startDate, endDate);

  return {
    entries,
    dateRange: {
      from: startDate,
      to: endDate,
    },
  };
});

const PAGE_SIZE = 1000;
const MAX_RECORDS = 50000;

/** Every record a paged endpoint holds, stopping at {@link MAX_RECORDS}. */
async function fetchAllPages<T>(
  label: string,
  fetchPage: (limit: number, offset: number) => Promise<{ data?: T[] }>
): Promise<T[]> {
  const all: T[] = [];
  for (let offset = 0; offset < MAX_RECORDS; offset += PAGE_SIZE) {
    const page = (await fetchPage(PAGE_SIZE, offset)).data ?? [];
    all.push(...page);
    if (page.length < PAGE_SIZE) return all;
  }
  console.warn(`${label} fetch reached safety limit of ${MAX_RECORDS} records`);
  return all;
}

/** Get boluses and carb intakes for a date range with pagination support */
export const getBolusesAndCarbs = query(
  DateRangeSchema.optional(),
  async (input) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;
    const { startDate, endDate } = await resolveReportRange(input);

    const [boluses, carbIntakes] = await Promise.all([
      fetchAllPages("Bolus", (limit, offset) =>
        apiClient.bolus.getAll(startDate, endDate, limit, offset)
      ),
      fetchAllPages("CarbIntake", (limit, offset) =>
        apiClient.nutrition.getCarbIntakes(startDate, endDate, limit, offset)
      ),
    ]);

    return {
      boluses,
      carbIntakes,
      dateRange: {
        from: startDate,
        to: endDate,
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
    population: z.enum(DiabetesPopulation).optional(),
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
      population,
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
    const { startDate, endDate } = await resolveReportRange(input);

    const [entries, { analysis, averagedStats, personalRange }] = await Promise.all([
      fetchAllGlucose(apiClient, startDate, endDate),
      apiClient.statistics.getRangeAnalytics(startDate, endDate),
    ]);

    return {
      entries,
      analysis,
      averagedStats,
      personalRange,
      dateRange: {
        from: startDate,
        to: endDate,
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
    const { startDate, endDate } = await resolveReportRange(input);

    const { analysis, averagedStats, hourlyBandThresholds, personalRange, contributingDevices } =
      await apiClient.statistics.getRangeAnalytics(startDate, endDate);

    return {
      analysis,
      averagedStats,
      hourlyBandThresholds,
      personalRange,
      contributingDevices,
      dateRange: {
        from: startDate,
        to: endDate,
      },
    };
  }
);

/**
 * Two of the patient's CGMs time-paired over a date range, with the agreement measures
 * over that pairing. Both devices come from the contributing-devices list on
 * {@link getReportsAnalysis} for the same range.
 */
export const getCgmComparison = query(
  DateRangeSchema.extend({
    deviceAId: z.string().uuid(),
    deviceBId: z.string().uuid(),
    toleranceMinutes: z.number().optional(),
  }),
  async (input) => {
    const { startDate, endDate } = await resolveReportRange(input);

    return compare({
      deviceAId: input.deviceAId,
      deviceBId: input.deviceBId,
      startDate,
      endDate,
      toleranceMinutes: input.toleranceMinutes,
    });
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
    const { startDate, endDate, timeZone, days } =
      await resolveReportRange(input);

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
      timeZone,
      days,
      dateRange: {
        from: startDate,
        to: endDate,
        lastUpdated: new Date().toISOString(),
      },
    };
  }
);

/**
 * Input schema for site change impact analysis. Uses nullish() for date fields
 * to match date-params hook.
 */
const SiteChangeImpactSchema = DateRangeSchema.extend({
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
    const { startDate, endDate } = await resolveReportRange(input);

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
        from: startDate,
        to: endDate,
      },
    };
  }
);

/** Per-weekday time-of-day glucose means for the week-to-week report. */
export const getWeekdayAverages = query(
  DateRangeSchema.optional(),
  async (input) => {
    const { locals } = getRequestEvent();
    const { startDate, endDate } = await resolveReportRange(input);
    return locals.apiClient.statistics.getWeekdayAverages(startDate, endDate);
  }
);

/** Per-hour figures and the ranked best and worst hours for the hourly-patterns report. */
export const getHourlyPatterns = query(
  DateRangeSchema.optional(),
  async (input) => {
    const { locals } = getRequestEvent();
    const { startDate, endDate } = await resolveReportRange(input);
    return locals.apiClient.statistics.getHourlyPatterns(startDate, endDate);
  }
);
