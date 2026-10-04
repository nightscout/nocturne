<script lang="ts">
  import type { DailySummaryDay, YearCalendarDatum } from "./calendar-datum";
  import { getWeekColumns } from "./week-columns";
  import YearHeatmap from "./YearHeatmap.svelte";

  let {
    year = 2024,
    yearData,
    onNavigate = () => {},
    cellFill = "var(--fixture-cell-fill)",
  }: {
    year?: number;
    yearData: Map<number, DailySummaryDay[]>;
    onNavigate?: (date: string) => void;
    cellFill?: string;
  } = $props();

  function transformYearData(days: DailySummaryDay[]): YearCalendarDatum[] {
    return days.map((day) => {
      const dateString = day.date ?? "";
      const [y, m, d] = dateString.split("-").map(Number);
      const counts = day.counts ?? {};
      return {
        date: new Date(y, m - 1, d),
        value: day.averageGlucoseMgdl ?? null,
        totalCount: day.totalCount ?? 0,
        filteredCount: Object.values(counts).reduce(
          (sum, count) => sum + count,
          0
        ),
        averageGlucoseMgdl: day.averageGlucoseMgdl ?? null,
        totalBolusUnits: day.totalBolusUnits ?? null,
        totalBasalUnits: day.totalBasalUnits ?? null,
        totalDailyDose: day.totalDailyDose ?? null,
        totalCarbs: day.totalCarbs ?? null,
        timeInRangePercent: day.timeInRangePercent ?? null,
        counts,
        dateString,
      };
    });
  }

  function getCellFill(): string {
    return cellFill;
  }

  function getVisibleCounts(
    counts: Record<string, number>
  ): [string, number][] {
    return Object.entries(counts)
      .filter(([, count]) => count > 0)
      .sort(([, a], [, b]) => b - a);
  }

  function formatUnits(value: number | null): string {
    return value == null ? "-" : `${value.toFixed(1)} U`;
  }
</script>

<YearHeatmap
  {year}
  yearIndex={0}
  loadingYears={new Set()}
  {yearData}
  {transformYearData}
  {getCellFill}
  {getWeekColumns}
  navigateToDayInReview={onNavigate}
  glucoseColorScale={() => "var(--fixture-glucose)"}
  units="mg/dl"
  unitLabel="mg/dL"
  {formatUnits}
  {getVisibleCounts}
/>
