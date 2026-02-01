import { getRequestEvent, query } from "$app/server";
import { z } from "zod";

const MONTH_NAMES = [
  "January", "February", "March", "April", "May", "June",
  "July", "August", "September", "October", "November", "December"
];

const punchCardSchema = z.object({
  fromDate: z.string(),
  toDate: z.string(),
});

export const getPunchCardData = query(punchCardSchema, async ({
  fromDate,
  toDate,
}) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  const startDate = new Date(fromDate);
  const endDate = new Date(toDate);

  if (isNaN(startDate.getTime()) || isNaN(endDate.getTime())) {
    return null;
  }

  // Set to full day boundaries
  startDate.setHours(0, 0, 0, 0);
  endDate.setHours(23, 59, 59, 999);

  // Fetch entries and treatments for the full range
  const entriesQuery = JSON.stringify({
    date: {
      $gte: startDate.toISOString(),
      $lte: endDate.toISOString(),
    },
  });

  const treatmentsQuery = JSON.stringify({
    created_at: {
      $gte: startDate.toISOString(),
      $lte: endDate.toISOString(),
    },
  });

  const [allEntries, allTreatments] = await Promise.all([
    apiClient.entries.getEntries2(entriesQuery, 100000),
    apiClient.treatments.getTreatments(undefined, 10000, undefined, treatmentsQuery),
  ]);

  // Group by month
  const monthsMap = new Map<string, {
    year: number;
    month: number;
    monthName: string;
    days: Array<{
      date: string;
      timestamp: number;
      totalReadings: number;
      inRangePercent: number;
      lowPercent: number;
      highPercent: number;
      averageGlucose: number;
      totalCarbs: number;
      totalInsulin: number;
      totalBolus: number;
      totalBasal: number;
      carbToInsulinRatio: number;
      inRangeCount: number;
      lowCount: number;
      highCount: number;
      entries: Array<{ mills: number; sgv: number }>;
    }>;
    maxCarbs: number;
    maxInsulin: number;
    maxCarbInsulinDiff: number;
    totalReadings: number;
  }>();

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

    const dayStart = new Date(currentDate);
    dayStart.setHours(0, 0, 0, 0);
    const dayEnd = new Date(currentDate);
    dayEnd.setHours(23, 59, 59, 999);

    const dayEntries = allEntries.filter((e) => {
      const entryTime = e.mills ?? new Date(e.dateString ?? "").getTime();
      return entryTime >= dayStart.getTime() && entryTime <= dayEnd.getTime();
    });

    const dayTreatments = allTreatments.filter((t) => {
      const treatmentTime = t.mills ?? new Date(t.createdAt ?? "").getTime();
      return treatmentTime >= dayStart.getTime() && treatmentTime <= dayEnd.getTime();
    });

    let tirMetrics = null;
    let treatmentSummary = null;

    if (dayEntries.length > 0) {
      tirMetrics = await apiClient.statistics.calculateTimeInRange({ entries: dayEntries });
    }
    if (dayTreatments.length > 0) {
      treatmentSummary = await apiClient.statistics.calculateTreatmentSummary(dayTreatments);
    }

    const percentages = tirMetrics?.percentages;
    const inRangePercent = percentages?.target ?? 0;
    const lowPercent = (percentages?.severeLow ?? 0) + (percentages?.low ?? 0);
    const highPercent = (percentages?.severeHigh ?? 0) + (percentages?.high ?? 0);

    const durations = tirMetrics?.durations;
    const totalMinutes = (durations?.severeLow ?? 0) + (durations?.low ?? 0) +
      (durations?.target ?? 0) + (durations?.high ?? 0) + (durations?.severeHigh ?? 0);
    const totalReadings = Math.round(totalMinutes / 5);

    const inRangeCount = Math.round((inRangePercent / 100) * totalReadings);
    const lowCount = Math.round((lowPercent / 100) * totalReadings);
    const highCount = Math.round((highPercent / 100) * totalReadings);

    const rangeStats = tirMetrics?.rangeStats;
    const averageGlucose = rangeStats?.target?.mean ?? rangeStats?.low?.mean ?? 0;

    const totals = treatmentSummary?.totals;
    const totalCarbs = totals?.food?.carbs ?? 0;
    const totalBolus = totals?.insulin?.bolus ?? 0;
    const totalBasal = totals?.insulin?.basal ?? 0;
    const totalInsulin = totalBolus + totalBasal;

    const carbToInsulinRatio = treatmentSummary?.carbToInsulinRatio ?? 0;

    const dateStr = currentDate.toISOString().split("T")[0];

    // Extract raw glucose entries for profile view (sorted by time)
    const entries = dayEntries
      .filter((e) => e.mills != null && e.sgv != null)
      .map((e) => ({ mills: e.mills!, sgv: e.sgv! }))
      .sort((a, b) => a.mills - b.mills);

    const dayStats = {
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
      entries,
    };

    const monthData = monthsMap.get(monthKey)!;
    monthData.days.push(dayStats);
    monthData.maxCarbs = Math.max(monthData.maxCarbs, dayStats.totalCarbs);
    monthData.maxInsulin = Math.max(monthData.maxInsulin, dayStats.totalInsulin);
    monthData.maxCarbInsulinDiff = Math.max(monthData.maxCarbInsulinDiff, Math.abs(dayStats.carbToInsulinRatio));
    monthData.totalReadings += dayStats.totalReadings;

    currentDate.setDate(currentDate.getDate() + 1);
  }

  const months = Array.from(monthsMap.values()).sort((a, b) => {
    if (a.year !== b.year) return a.year - b.year;
    return a.month - b.month;
  });

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
