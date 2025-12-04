import { getRequestEvent, query } from "$app/server";
import type { Entry, Treatment } from "$lib/api";
import { DEFAULT_THRESHOLDS } from "$lib/constants";

/**
 * Daily statistics for a single day
 */
export interface DayStats {
  date: string; // ISO date string (YYYY-MM-DD)
  timestamp: number; // Start of day in milliseconds

  // Glucose distribution
  totalReadings: number;
  inRangeCount: number;
  lowCount: number;
  highCount: number;
  inRangePercent: number;
  lowPercent: number;
  highPercent: number;
  averageGlucose: number;

  // Treatment totals
  totalCarbs: number;
  totalInsulin: number;
  totalBolus: number;
  totalBasal: number;

  // Calculated sizing factor
  carbToInsulinRatio: number;
}

/**
 * Month data aggregating all days
 */
export interface MonthData {
  year: number;
  month: number; // 0-11
  monthName: string;
  days: DayStats[];

  // Month totals for normalization
  maxCarbs: number;
  maxInsulin: number;
  maxCarbInsulinDiff: number;
  totalReadings: number;
}

/**
 * Response structure for month-to-month punch card data
 */
export interface PunchCardData {
  months: MonthData[];
  dateRange: {
    from: string;
    to: string;
  };
  // Global maximums for normalization across all months
  globalMaxCarbs: number;
  globalMaxInsulin: number;
  globalMaxCarbInsulinDiff: number;
}

const MONTH_NAMES = [
  "January", "February", "March", "April", "May", "June",
  "July", "August", "September", "October", "November", "December"
];

/**
 * Calculate daily statistics from entries and treatments
 */
function calculateDayStats(
  date: Date,
  entries: Entry[],
  treatments: Treatment[],
  thresholds = DEFAULT_THRESHOLDS
): DayStats {
  const dateStr = date.toISOString().split("T")[0];
  const dayStart = new Date(date);
  dayStart.setHours(0, 0, 0, 0);
  const dayEnd = new Date(date);
  dayEnd.setHours(23, 59, 59, 999);

  // Filter entries for this day
  const dayEntries = entries.filter((e) => {
    const entryTime = e.mills ?? new Date(e.dateString ?? "").getTime();
    return entryTime >= dayStart.getTime() && entryTime <= dayEnd.getTime();
  });

  // Filter treatments for this day
  const dayTreatments = treatments.filter((t) => {
    const treatmentTime = t.mills ?? new Date(t.createdAt ?? "").getTime();
    return treatmentTime >= dayStart.getTime() && treatmentTime <= dayEnd.getTime();
  });

  // Calculate glucose distribution
  const readings = dayEntries
    .filter((e) => e.sgv || e.mgdl)
    .map((e) => e.sgv ?? e.mgdl ?? 0);

  const totalReadings = readings.length;
  const lowThreshold = thresholds.low ?? 70;
  const highThreshold = thresholds.high ?? 180;

  const lowCount = readings.filter((r) => r < lowThreshold).length;
  const highCount = readings.filter((r) => r >= highThreshold).length;
  const inRangeCount = totalReadings - lowCount - highCount;

  const inRangePercent = totalReadings > 0 ? (inRangeCount / totalReadings) * 100 : 0;
  const lowPercent = totalReadings > 0 ? (lowCount / totalReadings) * 100 : 0;
  const highPercent = totalReadings > 0 ? (highCount / totalReadings) * 100 : 0;

  const averageGlucose = totalReadings > 0
    ? readings.reduce((sum, r) => sum + r, 0) / totalReadings
    : 0;

  // Calculate treatment totals
  let totalCarbs = 0;
  let totalInsulin = 0;
  let totalBolus = 0;
  let totalBasal = 0;

  for (const treatment of dayTreatments) {
    if (treatment.carbs) {
      totalCarbs += treatment.carbs;
    }
    if (treatment.insulin) {
      totalInsulin += treatment.insulin;
      if (treatment.eventType === "Bolus" || treatment.eventType === "Correction Bolus" || treatment.eventType === "Meal Bolus") {
        totalBolus += treatment.insulin;
      }
    }
    // Handle basal rate contributions
    if (treatment.eventType === "Temp Basal" && treatment.rate !== undefined && treatment.duration) {
      // Duration is in minutes, rate is U/hr
      const basalAmount = (treatment.rate * treatment.duration) / 60;
      totalBasal += basalAmount;
      totalInsulin += basalAmount;
    }
  }

  // Calculate carb to insulin ratio differential
  // Positive = more carbs relative to insulin, Negative = more insulin relative to carbs
  // Use a standard ratio (e.g., 10g carbs per 1U insulin) as baseline
  const standardCarbRatio = 10; // 10g carbs per 1U insulin
  const expectedInsulin = totalCarbs / standardCarbRatio;
  const carbToInsulinRatio = totalInsulin > 0
    ? (expectedInsulin - totalInsulin) / Math.max(expectedInsulin, totalInsulin, 1)
    : 0;

  return {
    date: dateStr,
    timestamp: dayStart.getTime(),
    totalReadings,
    inRangeCount,
    lowCount,
    highCount,
    inRangePercent,
    lowPercent,
    highPercent,
    averageGlucose,
    totalCarbs,
    totalInsulin,
    totalBolus,
    totalBasal,
    carbToInsulinRatio,
  };
}

/**
 * Query function to fetch punch card data for a date range
 */
export const getPunchCardData = query(async (
  fromDate: string,
  toDate: string
): Promise<PunchCardData | null> => {
  const event = getRequestEvent();
  if (!event) return null;

  const { apiClient } = event.locals;

  const startDate = new Date(fromDate);
  const endDate = new Date(toDate);

  // Validate dates
  if (isNaN(startDate.getTime()) || isNaN(endDate.getTime())) {
    return null;
  }

  // Fetch entries and treatments for the date range
  const entriesQuery = `find[date][$gte]=${startDate.toISOString()}&find[date][$lte]=${endDate.toISOString()}`;
  const treatmentsQuery = `find[created_at][$gte]=${startDate.toISOString()}&find[created_at][$lte]=${endDate.toISOString()}`;

  // Fetch all data with pagination
  const pageSize = 1000;
  let allEntries: Entry[] = [];
  let allTreatments: Treatment[] = [];

  // Fetch entries
  try {
    allEntries = await apiClient.entries.getEntries2(entriesQuery);
  } catch {
    allEntries = [];
  }

  // Fetch treatments with pagination
  let offset = 0;
  let hasMore = true;

  while (hasMore) {
    try {
      const batch = await apiClient.treatments.getTreatments2(treatmentsQuery, pageSize, offset);
      allTreatments = allTreatments.concat(batch);

      if (batch.length < pageSize) {
        hasMore = false;
      } else {
        offset += pageSize;
      }

      // Safety limit
      if (offset >= 50000) {
        hasMore = false;
      }
    } catch {
      hasMore = false;
    }
  }

  // Group days by month
  const monthsMap = new Map<string, MonthData>();

  // Iterate through each day in the range
  const currentDate = new Date(startDate);
  currentDate.setHours(0, 0, 0, 0);

  while (currentDate <= endDate) {
    const year = currentDate.getFullYear();
    const month = currentDate.getMonth();
    const monthKey = `${year}-${month}`;

    if (!monthsMap.has(monthKey)) {
      monthsMap.set(monthKey, {
        year,
        month,
        monthName: MONTH_NAMES[month],
        days: [],
        maxCarbs: 0,
        maxInsulin: 0,
        maxCarbInsulinDiff: 0,
        totalReadings: 0,
      });
    }

    const dayStats = calculateDayStats(currentDate, allEntries, allTreatments);
    const monthData = monthsMap.get(monthKey)!;

    monthData.days.push(dayStats);
    monthData.maxCarbs = Math.max(monthData.maxCarbs, dayStats.totalCarbs);
    monthData.maxInsulin = Math.max(monthData.maxInsulin, dayStats.totalInsulin);
    monthData.maxCarbInsulinDiff = Math.max(monthData.maxCarbInsulinDiff, Math.abs(dayStats.carbToInsulinRatio));
    monthData.totalReadings += dayStats.totalReadings;

    // Move to next day
    currentDate.setDate(currentDate.getDate() + 1);
  }

  // Convert to array and sort by date
  const months = Array.from(monthsMap.values()).sort((a, b) => {
    if (a.year !== b.year) return a.year - b.year;
    return a.month - b.month;
  });

  // Calculate global maximums
  let globalMaxCarbs = 0;
  let globalMaxInsulin = 0;
  let globalMaxCarbInsulinDiff = 0;

  for (const month of months) {
    globalMaxCarbs = Math.max(globalMaxCarbs, month.maxCarbs);
    globalMaxInsulin = Math.max(globalMaxInsulin, month.maxInsulin);
    globalMaxCarbInsulinDiff = Math.max(globalMaxCarbInsulinDiff, month.maxCarbInsulinDiff);
  }

  return {
    months,
    dateRange: {
      from: startDate.toISOString(),
      to: endDate.toISOString(),
    },
    globalMaxCarbs,
    globalMaxInsulin,
    globalMaxCarbInsulinDiff,
  };
});

/**
 * Query function to fetch detailed day data for the Day in Review report
 */
export const getDayInReviewData = query(async (dateStr: string): Promise<{
  date: string;
  entries: Entry[];
  treatments: Treatment[];
  stats: DayStats;
} | null> => {
  const event = getRequestEvent();
  if (!event) return null;

  const { apiClient } = event.locals;

  const date = new Date(dateStr);
  if (isNaN(date.getTime())) return null;

  const dayStart = new Date(date);
  dayStart.setHours(0, 0, 0, 0);
  const dayEnd = new Date(date);
  dayEnd.setHours(23, 59, 59, 999);

  const entriesQuery = `find[date][$gte]=${dayStart.toISOString()}&find[date][$lte]=${dayEnd.toISOString()}`;
  const treatmentsQuery = `find[created_at][$gte]=${dayStart.toISOString()}&find[created_at][$lte]=${dayEnd.toISOString()}`;

  let entries: Entry[] = [];
  let treatments: Treatment[] = [];

  try {
    entries = await apiClient.entries.getEntries2(entriesQuery);
  } catch {
    entries = [];
  }

  try {
    treatments = await apiClient.treatments.getTreatments2(treatmentsQuery);
  } catch {
    treatments = [];
  }

  const stats = calculateDayStats(date, entries, treatments);

  return {
    date: dateStr,
    entries,
    treatments,
    stats,
  };
});
