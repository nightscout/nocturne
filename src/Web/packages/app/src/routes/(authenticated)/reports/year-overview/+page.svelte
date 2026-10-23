<script lang="ts">
  import { browser } from "$app/environment";
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import CalendarDays from "@lucide/svelte/icons/calendar-days";
  import { scaleThreshold } from "d3-scale";
  import { Button } from "$lib/components/ui/button";
  import {
    getAvailableYears,
    getYearSummary,
  } from "$api/generated/dataOverviews.generated.remote";
  import GlycemicRiskIndexChart from "$lib/components/reports/GlycemicRiskIndexChart.svelte";
  import YearOverviewFilters from "$lib/components/reports/year-overview/YearOverviewFilters.svelte";
  import HeatmapLegend from "$lib/components/reports/year-overview/HeatmapLegend.svelte";
  import YearHeatmap from "$lib/components/reports/year-overview/YearHeatmap.svelte";
  import VisibleChart from "$lib/components/reports/year-overview/VisibleChart.svelte";
  import type {
    DailySummaryDay,
    GriTimelinePeriod,
    YearOverviewColorPreferences,
  } from "$api/generated/nocturne-api-client";
  import { getUnitLabel } from "$lib/utils/formatting";
  import {
    glucoseUnits,
    yearOverviewColors,
  } from "$lib/stores/appearance-store.svelte";
  import {
    glucoseScale,
    intensityScale,
    scaleFill,
    GLUCOSE_LIMITS,
    type ColorBoundary,
    type ColorFocusRange,
  } from "$lib/utils/metric-color-focus";
  import { onMount, untrack, tick } from "svelte";
  import { fade } from "svelte/transition";
  import { YearLoader } from "$lib/components/reports/year-overview/year-loader";
  import { toggled } from "$lib/utils/collections";
  import {
    getReportPrintContext,
    setReportPrintMeta,
    setReportPrintPreparation,
  } from "$lib/components/reports/print/report-print.svelte";
  import type { TextureKey } from "$lib/components/charts/print/chart-print-patterns";

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
  let failedYears = $state<Set<number>>(new Set());
  let metadataLoaded = $state(false);
  let metadataLoading = $state(false);
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

  /** Each metric's own theme color; glucose draws the heatmap ramp instead. */
  const METRIC_CSS_VARS: Record<Exclude<HeatmapMetric, "avgGlucose">, string> = {
    tir: "--glucose-in-range",
    bolus: "--insulin-bolus",
    basal: "--basal",
    tdd: "--insulin",
    carbs: "--carbs",
  };
  const METRIC_UNITS: Record<HeatmapMetric, string> = {
    avgGlucose: "",
    tir: "%",
    bolus: "U",
    basal: "U",
    tdd: "U",
    carbs: "g",
  };

  type ScaleKeys = {
    band: `${HeatmapMetric}Band`;
    boundaries: `${HeatmapMetric}Boundaries`;
  };
  const scaleKeys = $derived<ScaleKeys>({
    band: `${selectedMetric}Band`,
    boundaries: `${selectedMetric}Boundaries`,
  });
  /**
   * The retired shape applied its bands and dimming only in advanced mode. A blob saved with
   * advanced mode off carries settings its owner turned off, so they stay off.
   */
  const colorPreferences = $derived.by((): YearOverviewColorPreferences => {
    const stored = yearOverviewColors.current;
    if (!("advancedMode" in stored) || stored.advancedMode === true) return stored;
    return Object.fromEntries(
      Object.entries(stored).filter(
        ([key]) => !key.endsWith("Band") && key !== "outOfBandTransparency"
      )
    );
  });
  const dim = $derived(
    Math.max(0, Math.min(100, colorPreferences.outOfBandTransparency ?? 90))
  );

  /** The largest daily value of a dose or carb metric across the loaded years. */
  const metricMax = $derived.by(() => {
    let max = 0;
    for (const days of yearData.values()) {
      for (const day of days) {
        const value =
          selectedMetric === "bolus"
            ? day.totalBolusUnits
            : selectedMetric === "basal"
              ? day.totalBasalUnits
              : selectedMetric === "tdd"
                ? day.totalDailyDose
                : selectedMetric === "carbs"
                  ? day.totalCarbs
                  : undefined;
        if (value != null && Number.isFinite(value) && value > max) max = value;
      }
    }
    return max;
  });

  const scaleLimits = $derived<ColorFocusRange>(
    selectedMetric === "avgGlucose"
      ? GLUCOSE_LIMITS
      : [0, selectedMetric === "tir" ? 100 : Infinity]
  );
  const scale = $derived(
    selectedMetric === "avgGlucose"
      ? glucoseScale(
          colorPreferences.avgGlucoseBand,
          colorPreferences.avgGlucoseBoundaries,
          dim
        )
      : intensityScale(
          METRIC_CSS_VARS[selectedMetric],
          scaleLimits,
          metricMax,
          colorPreferences[scaleKeys.band],
          colorPreferences[scaleKeys.boundaries],
          dim
        )
  );

  /** Writes only the current shape, so keys from a retired one are dropped on the next save. */
  function saveColorPreferences(
    change: (next: YearOverviewColorPreferences) => void
  ) {
    const next: YearOverviewColorPreferences = {};
    if (colorPreferences.outOfBandTransparency !== undefined)
      next.outOfBandTransparency = colorPreferences.outOfBandTransparency;
    for (const { value } of METRIC_OPTIONS) {
      const band = colorPreferences[`${value}Band`];
      const boundaries = colorPreferences[`${value}Boundaries`];
      if (band) next[`${value}Band`] = band;
      if (boundaries) next[`${value}Boundaries`] = boundaries;
    }
    change(next);
    yearOverviewColors.current = next;
  }

  function setScaleWindow(focusWindow: ColorFocusRange | null) {
    saveColorPreferences((next) => {
      if (focusWindow) next[scaleKeys.band] = [...focusWindow];
      else delete next[scaleKeys.band];
    });
  }

  function setScaleBoundaries(boundaries: ColorBoundary[] | null) {
    saveColorPreferences((next) => {
      if (boundaries?.length)
        next[scaleKeys.boundaries] = boundaries.map((b) => ({ ...b }));
      else delete next[scaleKeys.boundaries];
    });
  }

  function setDim(value: number) {
    saveColorPreferences((next) => {
      next.outOfBandTransparency = Math.max(0, Math.min(100, value));
    });
  }

  function resetScale() {
    saveColorPreferences((next) => {
      delete next[scaleKeys.band];
      delete next[scaleKeys.boundaries];
    });
  }

  /** All known data types that can appear in counts */
  const ALL_DATA_TYPES = [
    "Glucose",
    "ManualBG",
    "BGChecks",
    "Boluses",
    "CarbIntake",
    "BolusCalculations",
    "Notes",
    "DeviceEvents",
    "TempBasals",
    "StateSpans",
    "Activity",
    "DeviceStatus",
  ];

  /** Data types currently hidden by the filter */
  let hiddenDataTypes = $state<Set<string>>(new Set());

  // =========================================================================
  // Glucose color scale
  // =========================================================================

  const GLUCOSE_BANDS = [54, 70, 180, 250];

  const glucoseColorScale = scaleThreshold<number, string>()
    .domain(GLUCOSE_BANDS)
    .range([
      "var(--glucose-very-low)",
      "var(--glucose-low)",
      "var(--glucose-in-range)",
      "var(--glucose-high)",
      "var(--glucose-very-high)",
    ]);

  const glucoseHatchScale = scaleThreshold<number, TextureKey | null>()
    .domain(GLUCOSE_BANDS)
    .range([
      "very-low-hatch",
      "low-hatch",
      null,
      "high-hatch",
      "very-high-hatch",
    ]);

  function getCellHatch(data: CalendarDatum | undefined): TextureKey | null {
    if (selectedMetric !== "avgGlucose" || data?.value == null) return null;
    return glucoseHatchScale(data.value);
  }

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

  function getCellFill(data: CalendarDatum | undefined): string {
    if (!data) return "rgb(0 0 0 / 5%)";
    const value =
      selectedMetric === "avgGlucose" ? data.value : getMetricCellValue(data);
    if (value != null && Number.isFinite(value)) return scaleFill(value, scale);
    if (data.filteredCount > 0) return "var(--muted)";
    return "rgb(0 0 0 / 5%)";
  }

  // =========================================================================
  // Derived
  // =========================================================================

  const units = $derived(glucoseUnits.current);
  const unitLabel = $derived(getUnitLabel(units));
  const sortedYears = $derived([...availableYears].sort((a, b) => b - a));

  setReportPrintMeta(() => {
    const title = "Year Overview";
    if (sortedYears.length === 0) return { title };
    const first = sortedYears.at(-1);
    const last = sortedYears[0];
    return {
      title,
      period: { label: first === last ? `${last}` : `${first} – ${last}` },
    };
  });

  /** Discover data types present in loaded data */
  const presentDataTypes = $derived.by(() => {
    const days = [...yearData.values()].flat();
    return ALL_DATA_TYPES.filter((t) =>
      days.some((day) => day.counts && Object.hasOwn(day.counts, t))
    );
  });

  // =========================================================================
  // Data Loading
  // =========================================================================

  let metadataRequest: Promise<void> | undefined;
  function loadMetadata(): Promise<void> {
    if (metadataRequest) return metadataRequest;
    metadataLoading = true;
    metadataRequest = (async () => {
      try {
        const result = await getAvailableYears().run();
        if (disposed) return;
        availableYears = result.years ?? [];
        availableDataSources = result.availableDataSources ?? [];
        metadataLoaded = true;
      } catch (err) {
        console.error("Failed to load available years:", err);
      } finally {
        metadataLoading = false;
        metadataRequest = undefined;
      }
    })();
    return metadataRequest;
  }

  const printContext = getReportPrintContext();
  let disposed = false;
  const yearLoader = new YearLoader(
    {
      summary: (year, sources) =>
        getYearSummary({
          year,
          dataSources: sources.length ? sources : undefined,
        }).run(),
    },
    {
      daily: (year, result) => {
        yearData = new Map([...yearData, [year, result.days ?? []]]);
      },
      gri: (year, result) => {
        griTimelineData = new Map([
          ...griTimelineData,
          [year, result.periods ?? []],
        ]);
      },
      loading: (year, loading) => {
        if (loading) failedYears = toggled(failedYears, year, false);
        loadingYears = toggled(loadingYears, year, loading);
        if (!loading)
          tick().then(() => {
            if (!disposed) setupObserver();
          });
      },
      error: (year, kind, error) => {
        failedYears = toggled(failedYears, year, true);
        console.error(`Failed to load ${kind} data for year ${year}:`, error);
      },
    }
  );

  function loadYearData(year: number) {
    return yearLoader.load(year);
  }

  setReportPrintPreparation(async () => {
    if (!metadataLoaded) await loadMetadata();
    if (!metadataLoaded || disposed)
      throw new Error("Year overview is unavailable");
    const sources = [...selectedDataSources].sort().join(",");
    for (const year of sortedYears) {
      if (
        !(await (failedYears.has(year)
          ? yearLoader.retry(year)
          : loadYearData(year)))
      )
        throw new Error("Year overview is incomplete");
    }
    if (disposed || sources !== [...selectedDataSources].sort().join(",")) {
      throw new Error("Year overview filters changed");
    }
    await tick();
  });

  function clearAndReload() {
    if (disposed) return;
    yearLoader.reset(selectedDataSources);
    yearData = new Map();
    griTimelineData = new Map();
    loadingYears = new Set();
    failedYears = new Set();
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
      const counts = day.counts ?? {};

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
    hiddenDataTypes = toggled(hiddenDataTypes, dataType);
  }

  function showAllDataTypes() {
    hiddenDataTypes = new Set();
  }

  // =========================================================================
  // IntersectionObserver for lazy loading
  // =========================================================================

  let observer: IntersectionObserver | undefined;

  function setupObserver() {
    if (!browser || disposed) return;

    observer?.disconnect();
    observer = new IntersectionObserver(
      (entries) => {
        if (yearLoader.busy || disposed) return;
        for (const entry of entries.toSorted(
          (a, b) =>
            Number(
              b.target instanceof HTMLElement ? b.target.dataset.year : 0
            ) -
            Number(a.target instanceof HTMLElement ? a.target.dataset.year : 0)
        )) {
          if (entry.isIntersecting) {
            if (!(entry.target instanceof HTMLElement)) continue;
            const year = Number(entry.target.dataset.year);
            if (!isNaN(year)) {
              if (yearLoader.canLoad(year)) {
                loadYearData(year);
                break;
              }
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

  function navigateToDayInReview(dateStr: string) {
    goto(resolve(`/reports/day-in-review?date=${dateStr}`));
  }

  // =========================================================================
  // Lifecycle
  // =========================================================================

  onMount(() => {
    // `.run()` rejects when called during the render/effect flush, so defer the
    // bootstrap to a microtask — onMount's synchronous body still counts as render.
    queueMicrotask(async () => {
      await loadMetadata();
      if (disposed) return;
      if (sortedYears.length > 0) {
        loadYearData(sortedYears[0]);
      }
    });
    return () => {
      disposed = true;
      yearLoader.dispose();
      observer?.disconnect();
    };
  });

  $effect(() => {
    void sortedYears;
    if (browser && metadataLoaded) {
      tick().then(() => {
        untrack(() => setupObserver());
      });
    }
    return () => {
      observer?.disconnect();
    };
  });

  // Re-fetch when data source filter changes
  $effect(() => {
    const currentKey = [...selectedDataSources].sort().join(",");
    const prevKey = [...prevDataSources].sort().join(",");
    if (currentKey !== prevKey && metadataLoaded) {
      prevDataSources = [...selectedDataSources];
      queueMicrotask(() => clearAndReload());
    }
  });

  // =========================================================================
  // Helpers
  // =========================================================================

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
</script>

<svelte:head>
  <title>Year Overview - Nocturne</title>
  <meta
    name="description"
    content="Multi-year heatmap overview of all your diabetes data"
  />
</svelte:head>

<div class="year-overview @container flex min-h-full">
  <!-- Main Content -->
  <div class="flex-1">
    {#if !metadataLoaded || sortedYears.some((year) => !yearData.has(year) || !griTimelineData.has(year))}
      <p class="hidden print:block">
        This overview is incomplete. Use the report's Print button to load all years before printing.
      </p>
    {/if}
    <!-- Header / interactive filters — hidden on print -->
    <div class="print:hidden">
      <YearOverviewFilters
        disabled={printContext?.printing ?? false}
        {availableDataSources}
        bind:selectedDataSources
        {presentDataTypes}
        {hiddenDataTypes}
        {toggleDataType}
        {showAllDataTypes}
      />
    </div>

    <!-- Color Legend -->
    <HeatmapLegend
      bind:selectedMetric
      metricOptions={METRIC_OPTIONS}
      {units}
      unit={METRIC_UNITS[selectedMetric]}
      {scale}
      limits={scaleLimits}
      onWindowChange={setScaleWindow}
      onBoundariesChange={setScaleBoundaries}
      onDimChange={setDim}
      onReset={resetScale}
      glucoseBands={GLUCOSE_BANDS}
    />

    <!-- Loading state for metadata -->
    {#if metadataLoading && !metadataLoaded}
      <div
        class="flex items-center justify-center py-20 print:hidden"
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
          <Button
            href="/settings/connectors"
            variant="outline"
            class="print:hidden"
          >
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
            failed={failedYears.has(year)}
            onRetry={() => yearLoader.retry(year)}
            {yearData}
            {transformYearData}
            {getCellFill}
            {getCellHatch}
            {navigateToDayInReview}
            {glucoseColorScale}
            {units}
            {unitLabel}
            {formatUnits}
            {getVisibleCounts}
            bind:sentinelElement={sentinelElements[year]}
          />

          {@const griPeriods = griTimelineData.get(year) ?? []}
          {#if griPeriods.length > 1}
            <div class="@container mt-4 border-t border-border pt-4">
              <VisibleChart label={`${year} monthly GRI chart`} eager={yearIndex === 0} placeholderClass="h-[calc(100cqw+15rem)] @md:h-[250px]">
                <GlycemicRiskIndexChart
                  gri={griPeriods[griPeriods.length - 1]?.gri ?? { score: 0 }}
                  timeSeriesData={griPeriods}
                />
              </VisibleChart>
            </div>
          {/if}
        {/each}
      </div>
    {/if}
  </div>
</div>

<style>
  /* The theme ramp runs black, cyan, green, yellow, red: its lightness rises and
     falls, so grey days read as either end. On paper it becomes one ramp that
     darkens with glucose; the band hatching over each cell tells lows apart. */
  @media print {
    .year-overview {
      --glucose-heatmap-1: oklch(0.97 0.02 250);
      --glucose-heatmap-2: oklch(0.93 0.04 245);
      --glucose-heatmap-3: oklch(0.87 0.07 225);
      --glucose-heatmap-4: oklch(0.8 0.1 190);
      --glucose-heatmap-5: oklch(0.72 0.12 150);
      --glucose-heatmap-6: oklch(0.63 0.13 95);
      --glucose-heatmap-7: oklch(0.53 0.15 55);
      --glucose-heatmap-8: oklch(0.44 0.15 30);
      --glucose-heatmap-9: oklch(0.32 0.12 20);
    }
  }
</style>
