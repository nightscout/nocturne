/**
 * Remote functions for reports data Provides sensor glucose, boluses, carb
 * intakes, device events, and analysis data for all report pages
 */
import { z } from "zod";
import { getRequestEvent, query } from "$app/server";
import { ClusterConfidence, GlucoseUnit, SleepSource } from "#lib/api/index.js";
import { fetchAllGlucose } from "./glucose-pagination";
import { DateRangeSchema, resolvePatientToday, resolveReportRange } from "./report-range";
import { readable } from "#lib/server/patient-timezone.js";
import { compare } from "#lib/api/generated/cgmComparisons.generated.remote.js";
import { getTrends } from "#lib/api/generated/sleepReports.generated.remote.js";
import {
  getBasalAnalysis,
  getDailyBasalBolusRatios,
  getHourlyInsulinDelivery,
  getInsulinDeliveryStatistics,
} from "#lib/api/generated/statistics.generated.remote.js";

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

/** The patient's current calendar day as `YYYY-MM-DD`, which reports build relative windows from. */
export const getReportsToday = query(async () => resolvePatientToday());

/**
 * The instants a picked range spans: midnight opening its first day through the last millisecond
 * of its last day, both read in the patient's zone. For a view that draws the range itself.
 */
export const getReportRange = query(DateRangeSchema, async (input) => {
  const { startDate, endDate } = await resolveReportRange(input);
  return { startDate, endDate };
});

/** Basal delivery statistics, temp basal counts and hourly percentiles over the patient's days. */
export const getBasalAnalysisReport = query(DateRangeSchema, async (input) => {
  const { startDate, endDate } = await resolveReportRange(input, 14);
  return getBasalAnalysis({ startDate, endDate });
});

/** Hourly insulin delivery over the patient's days. */
export const getHourlyDeliveryReport = query(DateRangeSchema, async (input) => {
  const { startDate, endDate } = await resolveReportRange(input, 14);
  return getHourlyInsulinDelivery({ startDate, endDate });
});

/** Headline insulin figures over the patient's days. */
export const getInsulinDeliveryReport = query(DateRangeSchema, async (input) => {
  const { startDate, endDate } = await resolveReportRange(input, 30);
  return getInsulinDeliveryStatistics({ startDate, endDate });
});

/** Daily basal and bolus totals against carbs over the patient's days. */
export const getDailyBasalBolusReport = query(DateRangeSchema, async (input) => {
  const { startDate, endDate } = await resolveReportRange(input, 30);
  return getDailyBasalBolusRatios({ startDate, endDate });
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

/**
 * How many sensor readings a date range holds. The pages show the count, not the readings: one
 * row and the total rather than every reading of the window.
 */
export const getReadingCount = query(
  DateRangeSchema.optional(),
  async (input) => {
    const { apiClient } = getRequestEvent().locals;
    const { startDate, endDate } = await resolveReportRange(input);
    const readings = await apiClient.sensorGlucose.getAll(
      startDate,
      endDate,
      1,
      0,
      undefined,
      undefined,
      undefined,
      true
    );

    return {
      readingCount: readings.pagination?.total ?? 0,
      dateRange: {
        from: startDate,
        to: endDate,
      },
    };
  }
);

/**
 * Server-side extended analytics and averaged stats for a date range: the overview, executive
 * summary, AGP and glucose distribution reports render these computed metrics.
 */
export const getReportsAnalysis = query(
  DateRangeSchema.optional(),
  async (input) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;
    const { startDate, endDate } = await resolveReportRange(input);

    const {
      analysis,
      averagedStats,
      hourlyBandThresholds,
      personalRange,
      contributingDevices,
      timeZoneUnavailableReason,
    } = await apiClient.statistics.getRangeAnalytics(startDate, endDate);

    return {
      analysis,
      averagedStats,
      hourlyBandThresholds,
      personalRange,
      contributingDevices,
      timeZoneUnavailableReason,
      dateRange: {
        from: startDate,
        to: endDate,
        lastUpdated: new Date().toISOString(),
      },
    };
  }
);

/**
 * Two periods' glucose and insulin figures with each metric's change from the first to the second,
 * which the comparison report renders as served. Glucose is served in `units`.
 */
export const getReportsComparison = query(
  z.object({ a: DateRangeSchema, b: DateRangeSchema, units: z.enum(GlucoseUnit) }),
  async ({ a, b, units }) => {
    const { apiClient } = getRequestEvent().locals;
    const [rangeA, rangeB] = await Promise.all([resolveReportRange(a), resolveReportRange(b)]);
    return apiClient.statistics.getPeriodComparison(
      rangeA.startDate,
      rangeA.endDate,
      rangeB.startDate,
      rangeB.endDate,
      units
    );
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
 * Multi-night sleep trends over a date range. The bounds are the patient's days, as the API
 * counts them against its span limit.
 */
export const getSleepTrends = query(
  DateRangeSchema.extend({ source: z.enum(SleepSource).optional() }),
  async (input) => {
    const { startDate, endDate } = await resolveReportRange(input);

    return getTrends({ from: startDate, to: endDate, source: input.source });
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
