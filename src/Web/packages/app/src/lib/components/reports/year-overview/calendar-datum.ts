import type { DailySummaryDay } from "#lib/api/index.js";

/** One day cell of the year-overview heatmap, as the page derives it. */
export interface YearCalendarDatum {
  date: Date;
  value: number | null;
  totalCount: number;
  filteredCount: number;
  averageGlucoseMgdl: number | null;
  totalBolusUnits: number | null;
  totalBasalUnits: number | null;
  totalDailyDose: number | null;
  totalCarbs: number | null;
  timeInRangePercent: number | null;
  counts: Record<string, number>;
  dateString: string;
}

export type { DailySummaryDay };
