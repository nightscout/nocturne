<script lang="ts">
  import { goto } from "$app/navigation";
  import { browser } from "$app/environment";
  import { Loader2, CalendarDays } from "lucide-svelte";
  import { scaleThreshold, scaleLinear } from "d3-scale";
  import { Button } from "$lib/components/ui/button";
  import {
    getAvailableYears,
    getDailySummary,
    getGriTimeline,
  } from "$api/generated/dataOverviews.generated.remote";
  import GlycemicRiskIndexChart from "$lib/components/reports/GlycemicRiskIndexChart.svelte";
  import YearOverviewFilters from "$lib/components/reports/year-overview/YearOverviewFilters.svelte";
  import HeatmapLegend from "$lib/components/reports/year-overview/HeatmapLegend.svelte";
  import YearHeatmap from "$lib/components/reports/year-overview/YearHeatmap.svelte";
  import DayDetailPanel from "$lib/components/reports/year-overview/DayDetailPanel.svelte";
  import type {
    DailySummaryDay,
    GriTimelinePeriod,
  } from "$api/generated/nocturne-api-client";
  import { getUnitLabel } from "$lib/utils/formatting";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { getDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { fade } from "svelte/transition";

  const reportsParams = getDateParamsContext();

  // =========================================================================
  // State
  // =========================================================================

  let availableYears = $state<number[]>([]);
  let availableDataSources = $state<string[]>([]);
  let selectedDataSources = $state<string[]>([]);
  let prevDataSources = $state<string[]>([]);
  let yearData = $state<Map<number, DailySummaryDay[]>>(new Map());
  let griTimelineData = $state<Map<number, GriTimelinePeriod[]>>(new Map());
  let loadingYears = $state<Set<number>>(new Set());
  let metadataLoaded = $state(false);
  let metadataLoading = $state(false);
  let selectedDay = $state<CalendarDatum | null>(null);
  let sentinelElements: Record<number, HTMLDivElement | undefined> = $state({});

  type HeatmapMetric =
    | "avgGlucose"
    | "tir"
    | "bolus"
    | "basal"
    | "tdd"
    | "carbs";

  const METRIC_OPTIONS: { value: HeatmapMetric; label: string }[] = [
    { value: "avgGlucose", label: "Avg Glucose" },
    { value: "tir", label: "Time in Range" },
    { value: "bolus", label: "Bolus" },
    { value: "basal", label: "Basal" },
    { value: "tdd", label: "TDD" },
    { value: "carbs", label: "Carbs" },
  ];

  let selectedMetric = $state<HeatmapMetric>("avgGlucose");

  /** All known data types that can appear in counts */
  const ALL_DATA_TYPES = [
    "Glucose",
    "ManualBG",
    "Boluses",
    "CarbIntake",
    "BolusCalculations",
    "Notes",
    "DeviceEvents",
    "StateSpans",
    "Activity",
    "DeviceStatus",
  ];

  /** Data types currently hidden by the filter */
  let hiddenDataTypes = $state<Set<string>>(new Set());

  // =========================================================================
  // Glucose color scale
  // =========================================================================

  const glucoseColorScale = scaleThreshold<number, string>()
    .domain([54, 70, 180, 250])
    .range([
      "var(--glucose-very-low)",
      "var(--glucose-low)",
      "var(--glucose-in-range)",
      "var(--glucose-high)",
      "var(--glucose-very-high)",
    ]);

  /**
   * Multi-hue heatmap scale — maximises perceptual distinction in the 70–250
   * range
   */
  const HEATMAP_DOMAIN = [40, 54, 70, 100, 140, 180, 220, 260, 350];
  const HEATMAP_COLORS = [
    "#2563eb", // blue-600   — critically low
    "#3b82f6", // blue-500   — very low
    "#06b6d4", // cyan-500   — low
    "#10b981", // emerald-500 — on target
    "#84cc16", // lime-500   — upper in-range
    "#eab308", // yellow-500  — entering high
    "#f97316", // orange-500  — high
    "#ef4444", // red-500    — very high
    "#b91c1c", // red-700    — critically high
  ];

  const heatmapScale = scaleLinear<string>()
    .domain(HEATMAP_DOMAIN)
    .range(HEATMAP_COLORS)
    .clamp(true);

  const LEGEND_W = 420;
  const LEGEND_THRESHOLDS = [70, 180, 250];

  function legendX(mgdl: number): number {
    return ((mgdl - 40) / 310) * LEGEND_W;
  }

  /** CSS variable names for each metric's hue */
  const METRIC_CSS_VARS: Record<
    Exclude<HeatmapMetric, "avgGlucose">,
    string
  > = {
    tir: "--chart-2",
    bolus: "--chart-1",
    basal: "--chart-3",
    tdd: "--chart-4",
    carbs: "--chart-5",
  };

  /** Compute max value for a metric across all loaded year data */
  function getMetricMax(metric: HeatmapMetric): number {
    let max = 0;
    for (const days of yearData.values()) {
      for (const day of days) {
        let val: number | undefined | null;
        switch (metric) {
          case "bolus":
            val = day.totalBolusUnits;
            break;
          case "basal":
            val = day.totalBasalUnits;
            break;
          case "tdd":
            val = day.totalDailyDose;
            break;
          case "carbs":
            val = day.totalCarbs;
            break;
          case "tir":
            val = day.timeInRangePercent;
            break;
          default:
            val = day.averageGlucoseMgdl;
            break;
        }
        if (val != null && val > max) max = val;
      }
    }
    return max || 1;
  }

  /**
   * Memoized max for current metric — recomputed only when metric or data
   * changes
   */
  const metricMaxCached = $derived.by(() => {
    // Depend on yearData and selectedMetric
    void yearData;
    if (selectedMetric === "avgGlucose") return 1;
    if (selectedMetric === "tir") return 100;
    return getMetricMax(selectedMetric);
  });

  /** Get cell value for the selected metric */
  function getMetricCellValue(data: CalendarDatum): number | null {
    switch (selectedMetric) {
      case "tir":
        return data.timeInRangePercent;
      case "bolus":
        return data.totalBolusUnits;
      case "basal":
        return data.totalBasalUnits;
      case "tdd":
        return data.totalDailyDose;
      case "carbs":
        return data.totalCarbs;
      default:
        return null;
    }
  }

  function getIntensityFill(
    value: number,
    maxVal: number,
    cssVarName: string
  ): string {
    const intensity = Math.min(value / maxVal, 1);
    // Scale from 15% opacity (min visible) to 100%
    const alpha = 0.15 + intensity * 0.85;
    return `color-mix(in srgb, var(${cssVarName}) ${Math.round(alpha * 100)}%, transparent)`;
  }

  function getCellFill(data: CalendarDatum | undefined): string {
    if (!data) return "rgb(0 0 0 / 5%)";

    if (selectedMetric === "avgGlucose") {
      if (data.value != null) return heatmapScale(data.value);
      if (data.filteredCount > 0) return "hsl(var(--muted))";
      return "rgb(0 0 0 / 5%)";
    }

    const metricValue = getMetricCellValue(data);
    if (metricValue == null) {
      if (data.filteredCount > 0) return "hsl(var(--muted))";
      return "rgb(0 0 0 / 5%)";
    }

    const maxVal = metricMaxCached;
    const cssVar =
      METRIC_CSS_VARS[selectedMetric as Exclude<HeatmapMetric, "avgGlucose">];
    return getIntensityFill(metricValue, maxVal, cssVar);
  }

  // =========================================================================
  // Derived
  // =========================================================================

  const units = $derived(glucoseUnits.current);
  const unitLabel = $derived(getUnitLabel(units));
  const sortedYears = $derived([...availableYears].sort((a, b) => b - a));

  /** Discover data types present in loaded data */
  const presentDataTypes = $derived.by(() => {
    const types = new Set<string>();
    for (const days of yearData.values()) {
      for (const day of days) {
        if (day.counts) {
          for (const key of Object.keys(day.counts) as string[]) {
            types.add(key);
          }
        }
      }
    }
    return ALL_DATA_TYPES.filter((t) => types.has(t));
  });

  // =========================================================================
  // Data Loading
  // =========================================================================

  async function loadMetadata() {
    if (metadataLoading) return;
    metadataLoading = true;
    try {
      const result = await getAvailableYears().run();
      availableYears = result.years ?? [];
      availableDataSources = result.availableDataSources ?? [];
      metadataLoaded = true;
    } catch (err) {
      console.error("Failed to load available years:", err);
    } finally {
      metadataLoading = false;
    }
  }

  async function loadYearData(year: number) {
    if (loadingYears.has(year) || yearData.has(year)) return;

    loadingYears = new Set([...loadingYears, year]);
    try {
      const params: { year: number; dataSources?: string[] } = { year };
      if (selectedDataSources.length > 0) {
        params.dataSources = selectedDataSources;
      }
      const result = await getDailySummary(params).run();
      const days = result.days ?? [];
      yearData = new Map([...yearData, [year, days]]);
      loadGriTimeline(year);
    } catch (err) {
      console.error(`Failed to load data for year ${year}:`, err);
    } finally {
      const next = new Set(loadingYears);
      next.delete(year);
      loadingYears = next;
    }
  }

  async function loadGriTimeline(year: number) {
    if (griTimelineData.has(year)) return;
    try {
      const result = await getGriTimeline({
        year,
        dataSources:
          selectedDataSources.length > 0 ? selectedDataSources : undefined,
      }).run();
      const periods = result.periods ?? [];
      griTimelineData = new Map([...griTimelineData, [year, periods]]);
    } catch (err) {
      console.error(`Failed to load GRI timeline for year ${year}:`, err);
    }
  }

  function clearAndReload() {
    yearData = new Map();
    griTimelineData = new Map();
    loadingYears = new Set();
    if (sortedYears.length > 0) {
      loadYearData(sortedYears[0]);
    }
  }

  // =========================================================================
  // Chart data transformation
  // =========================================================================

  type CalendarDatum = {
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
  };

  function transformYearData(days: DailySummaryDay[]): CalendarDatum[] {
    return days.map((day) => {
      const dateStr = day.date ?? "";
      const [y, m, d] = dateStr.split("-").map(Number);
      const date = new Date(y, m - 1, d);
      const avg = day.averageGlucoseMgdl ?? null;
      const counts = (day.counts as Record<string, number>) ?? {};

      // Calculate filtered count excluding hidden types
      const filteredCount = Object.entries(counts)
        .filter(([key]) => !hiddenDataTypes.has(key))
        .reduce((sum, [, count]) => sum + count, 0);

      return {
        date,
        value: avg,
        totalCount: day.totalCount ?? 0,
        filteredCount,
        averageGlucoseMgdl: avg,
        totalBolusUnits: day.totalBolusUnits ?? null,
        totalBasalUnits: day.totalBasalUnits ?? null,
        totalDailyDose: day.totalDailyDose ?? null,
        totalCarbs: day.totalCarbs ?? null,
        timeInRangePercent: day.timeInRangePercent ?? null,
        counts,
        dateString: dateStr,
      };
    });
  }

  // =========================================================================
  // Data type filter
  // =========================================================================

  function toggleDataType(dataType: string) {
    const next = new Set(hiddenDataTypes);
    if (next.has(dataType)) {
      next.delete(dataType);
    } else {
      next.add(dataType);
    }
    hiddenDataTypes = next;
  }

  function showAllDataTypes() {
    hiddenDataTypes = new Set();
  }

  // =========================================================================
  // IntersectionObserver for lazy loading
  // =========================================================================

  let observer: IntersectionObserver | undefined;

  function setupObserver() {
    if (!browser) return;

    observer?.disconnect();
    observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (entry.isIntersecting) {
            const year = Number((entry.target as HTMLElement).dataset.year);
            if (!isNaN(year)) {
              loadYearData(year);
            }
          }
        }
      },
      { rootMargin: "200px" }
    );

    for (const year of sortedYears) {
      const el = sentinelElements[year];
      if (el) observer.observe(el);
    }
  }

  // =========================================================================
  // Day detail panel
  // =========================================================================

  function closeDetailPanel() {
    selectedDay = null;
  }

  function navigateToDayInReview(dateStr: string) {
    if (reportsParams) {
      reportsParams.setCustomRange(dateStr, dateStr);
    }
    goto(
      `/reports/day-in-review?from=${dateStr}&to=${dateStr}&isDefault=false`
    );
  }

  // =========================================================================
  // Lifecycle
  // =========================================================================

  $effect(() => {
    if (browser && !metadataLoaded && !metadataLoading) {
      loadMetadata();
    }
  });

  $effect(() => {
    if (metadataLoaded && sortedYears.length > 0) {
      loadYearData(sortedYears[0]);
    }
  });

  $effect(() => {
    void sentinelElements;
    if (browser && metadataLoaded) {
      setupObserver();
    }
    return () => {
      observer?.disconnect();
    };
  });

  // Re-fetch when data source filter changes
  $effect(() => {
    const currentKey = selectedDataSources.sort().join(",");
    const prevKey = prevDataSources.sort().join(",");
    if (currentKey !== prevKey && metadataLoaded) {
      prevDataSources = [...selectedDataSources];
      clearAndReload();
    }
  });

  // =========================================================================
  // Helpers
  // =========================================================================

  function getYearBounds(year: number): { start: Date; end: Date } {
    return {
      start: new Date(year, 0, 1),
      end: new Date(year, 11, 31),
    };
  }

  function formatSelectedDate(dateStr: string): string {
    const [y, m, d] = dateStr.split("-").map(Number);
    const date = new Date(y, m - 1, d);
    return date.toLocaleDateString(undefined, {
      weekday: "long",
      year: "numeric",
      month: "long",
      day: "numeric",
    });
  }

  function formatUnits(value: number | null): string {
    if (value == null) return "-";
    return value.toFixed(1) + " U";
  }

  /** Get visible counts (filtered by hiddenDataTypes) */
  function getVisibleCounts(
    counts: Record<string, number>
  ): [string, number][] {
    return Object.entries(counts)
      .filter(([key, count]) => count > 0 && !hiddenDataTypes.has(key))
      .sort(([, a], [, b]) => b - a);
  }

  /** Get ISO week number for a date */
  function getISOWeekNumber(date: Date): number {
    const d = new Date(
      Date.UTC(date.getFullYear(), date.getMonth(), date.getDate())
    );
    d.setUTCDate(d.getUTCDate() + 4 - (d.getUTCDay() || 7));
    const yearStart = new Date(Date.UTC(d.getUTCFullYear(), 0, 1));
    return Math.ceil(((d.getTime() - yearStart.getTime()) / 86400000 + 1) / 7);
  }

  /** Get the Monday and Sunday of the ISO week containing the given date */
  function getWeekBounds(date: Date): { from: string; to: string } {
    const d = new Date(date.getFullYear(), date.getMonth(), date.getDate());
    const day = d.getDay();
    const diffToMonday = day === 0 ? -6 : 1 - day;
    const monday = new Date(d);
    monday.setDate(d.getDate() + diffToMonday);
    const sunday = new Date(monday);
    sunday.setDate(monday.getDate() + 6);
    const fmt = (dt: Date) =>
      `${dt.getFullYear()}-${String(dt.getMonth() + 1).padStart(2, "0")}-${String(dt.getDate()).padStart(2, "0")}`;
    return { from: fmt(monday), to: fmt(sunday) };
  }

  type WeekColumn = {
    x: number;
    weekNumber: number;
    from: string;
    to: string;
  };

  /** Extract unique week columns from calendar cells */
  function getWeekColumns(
    cells: Array<{ x: number; data?: { date?: Date } }>
  ): WeekColumn[] {
    const seen = new Map<number, { date: Date }>();
    for (const cell of cells) {
      const date = cell.data?.date;
      if (date && !seen.has(cell.x)) {
        seen.set(cell.x, { date });
      }
    }
    return [...seen.entries()]
      .map(([x, { date }]) => ({
        x,
        weekNumber: getISOWeekNumber(date),
        ...getWeekBounds(date),
      }))
      .sort((a, b) => a.x - b.x);
  }
</script>

<svelte:head>
  <title>Year Overview - Nocturne</title>
  <meta
    name="description"
    content="Multi-year heatmap overview of all your diabetes data"
  />
</svelte:head>

<div class="flex min-h-full">
  <!-- Main Content -->
  <div
    class="flex-1 transition-[margin] duration-200 {selectedDay
      ? 'mr-80 lg:mr-96'
      : ''}"
  >
    <!-- Header -->
    <YearOverviewFilters
      {availableDataSources}
      bind:selectedDataSources
      {presentDataTypes}
      {hiddenDataTypes}
      {toggleDataType}
      {showAllDataTypes}
    />

    <!-- Color Legend -->
    <HeatmapLegend
      bind:selectedMetric
      {units}
      {METRIC_OPTIONS}
      {HEATMAP_DOMAIN}
      {HEATMAP_COLORS}
      {LEGEND_W}
      {LEGEND_THRESHOLDS}
      {legendX}
      {METRIC_CSS_VARS}
      {getMetricMax}
    />

    <!-- Loading state for metadata -->
    {#if metadataLoading && !metadataLoaded}
      <div
        class="flex items-center justify-center py-20"
        in:fade={{ duration: 200 }}
      >
        <div class="flex flex-col items-center gap-3">
          <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
          <p class="text-sm text-muted-foreground">Loading data overview...</p>
        </div>
      </div>
    {/if}

    <!-- No data state -->
    {#if metadataLoaded && sortedYears.length === 0}
      <div
        class="flex items-center justify-center py-20"
        in:fade={{ duration: 300 }}
      >
        <div class="max-w-md space-y-4 text-center">
          <div
            class="mx-auto flex h-16 w-16 items-center justify-center rounded-full bg-muted"
          >
            <CalendarDays class="h-8 w-8 text-muted-foreground" />
          </div>
          <h2 class="text-xl font-semibold">No Data Available</h2>
          <p class="text-muted-foreground">
            There is no data to display yet. Connect a data source in your
            settings to get started.
          </p>
          <Button href="/settings/connectors" variant="outline">
            Configure Data Sources
          </Button>
        </div>
      </div>
    {/if}

    <!-- Year Calendars -->
    {#if metadataLoaded && sortedYears.length > 0}
      <div class="space-y-10">
        {#each sortedYears as year, yearIndex (year)}
          <YearHeatmap
            {year}
            {yearIndex}
            {loadingYears}
            {yearData}
            {getYearBounds}
            {transformYearData}
            {getCellFill}
            {getWeekColumns}
            {navigateToDayInReview}
            {glucoseColorScale}
            {units}
            {unitLabel}
            {formatUnits}
            {getVisibleCounts}
            bind:sentinelElement={sentinelElements[year]}
          />

          <!-- GRI Chart for year -->
          {@const griPeriods = griTimelineData.get(year) ?? []}
          {#if griPeriods.length > 1}
            <div class="mt-4 rounded-lg border border-border bg-card p-4">
              <GlycemicRiskIndexChart
                gri={griPeriods[griPeriods.length - 1]?.gri ?? { score: 0 }}
                timeSeriesData={griPeriods}
              />
            </div>
          {/if}
        {/each}
      </div>
    {/if}
  </div>

  <!-- Day Detail Panel -->
  <DayDetailPanel
    {selectedDay}
    {units}
    {unitLabel}
    {formatSelectedDate}
    {formatUnits}
    {glucoseColorScale}
    {getVisibleCounts}
    {closeDetailPanel}
    {navigateToDayInReview}
  />
</div>
