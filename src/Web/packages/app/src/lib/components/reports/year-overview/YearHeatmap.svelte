<script lang="ts">
  import { resolve } from "$app/paths";
  import { Chart, Layer, Path, Tooltip } from "layerchart";
  import { scaleThreshold } from "d3-scale";
  import { timeMonth, timeMonths } from "d3-time";
  import { Button } from "#lib/components/ui/button/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import { fly } from "svelte/transition";
  import { cubicOut } from "svelte/easing";
  import {
    formatGlucoseValue,
    formatLocale,
    formatMonthLabel,
    formatShortDate,
    formatWeekdayDate,
  } from "#lib/utils/formatting.js";
  import { toDayString } from "#lib/utils/date-range.js";
  import { firstDayOfWeek } from "#lib/components/calendar/calendar-date.js";
  import type { GlucoseUnits } from "#lib/utils/formatting.js";
  import { getDataTypeLabel } from "#lib/utils/data-type-labels.js";
  import { yearCalendarBounds } from "./year-bounds";
  import { layoutYear, monthOutlinePath } from "./week-layout";
  import VisibleChart from "./VisibleChart.svelte";
  import { PrintMode } from "#lib/components/charts/print/print-mode.svelte.js";
  import { patternClass, type TextureKey } from "#lib/components/charts/print/chart-print-patterns.js";
  import type { DailySummaryDay, YearCalendarDatum } from "./calendar-datum";


  let {
    year,
    yearIndex,
    loadingYears,
    failed = false,
    onRetry,
    yearData,
    transformYearData,
    getCellFill,
    getCellHatch,
    navigateToDayInReview,
    glucoseColorScale,
    glucoseBands,
    units,
    unitLabel,
    formatUnits,
    getVisibleCounts,
    sentinelElement = $bindable()

    /** Texture laid over a cell's fill to mark its band in black and white. */
    /** Very-low, low, high and very-high boundaries in mg/dL, in the order the colour ranges run. */
  }: { 
    year: number;
    yearIndex: number;
    loadingYears: Set<number>;
    failed?: boolean;
    onRetry?: () => void;
    yearData: Map<number, DailySummaryDay[]>;
    transformYearData: (days: DailySummaryDay[]) => YearCalendarDatum[];
    getCellFill: (data: YearCalendarDatum | undefined) => string;
    getCellHatch?: (data: YearCalendarDatum | undefined) => TextureKey | null;
    navigateToDayInReview: (dateStr: string) => void;
    glucoseColorScale: (mgdl: number) => string;
    glucoseBands: readonly number[];
    units: GlucoseUnits;
    unitLabel: string;
    formatUnits: (value: number | null) => string;
    getVisibleCounts: (counts: Record<string, number>) => [string, number][];
    sentinelElement?: HTMLDivElement;
  } = $props();

  const days = $derived(yearData.get(year));
  const chartData = $derived(days ? transformYearData(days) : []);
  const isYearLoading = $derived(loadingYears.has(year) && !days);

  const print = new PrintMode();
  let chartWidth = $state(0);

  /**
   * Screen scrolls a full year of fixed-size cells. Paper cannot scroll, so a
   * print keeps only the months that hold data and sizes cells to fit the width.
   */
  const bounds = $derived.by(() => {
    const full = yearCalendarBounds(year);
    if (!print.active) return full;
    const dated = chartData.filter((d) => d.totalCount > 0).map((d) => d.date.getTime());
    if (dated.length === 0) return full;
    return {
      start: timeMonth.floor(new Date(Math.min(...dated))),
      end: timeMonth.offset(timeMonth.floor(new Date(Math.max(...dated))), 1),
    };
  });
  const SCREEN_CELL = 24;
  const PRINT_CELL_MAX = 32;
  const firstDay = $derived(firstDayOfWeek(formatLocale()));
  const layout = $derived(layoutYear(bounds.start, bounds.end, firstDay));
  const columnCount = $derived(layout.columns.length);
  const datumByDay = $derived(new Map(chartData.map((d) => [d.dateString, d])));
  const cellSize = $derived(
    print.active && chartWidth > 0
      ? Math.min(PRINT_CELL_MAX, chartWidth / columnCount)
      : SCREEN_CELL
  );
</script>

<div
  class="@container"
  in:fly={{
    y: 30,
    duration: 500,
    delay: Math.min(yearIndex * 100, 300),
    easing: cubicOut,
  }}
>
  <!-- Sentinel for IntersectionObserver -->
  <div
    data-year={year}
    bind:this={sentinelElement}
    class="pointer-events-none h-0"
  ></div>

  <!-- Year Label -->
  <div class="mb-3 flex flex-wrap items-center gap-x-3 gap-y-1">
    <h2 class="text-xl font-bold tabular-nums">{year}</h2>
    {#if failed}
      <span class="text-sm text-destructive">Some year data could not be loaded.</span>
      <Button variant="outline" size="sm" onclick={onRetry} disabled={loadingYears.has(year)}>Retry {year}</Button>
    {/if}
    {#if isYearLoading}
      <Loader2 class="h-4 w-4 animate-spin text-muted-foreground" />
    {/if}
    {#if days}
      <span class="text-sm text-muted-foreground">
        {days.filter((d) => (d.totalCount ?? 0) > 0).length} days with data
      </span>
    {/if}
  </div>

  <!-- Calendar Heatmap -->
  {#if chartData.length > 0}
    <div
      class="w-full overflow-x-auto overflow-y-visible rounded-lg border border-border bg-card p-4 print:overflow-visible"
    >
      <div
        class="h-60 min-w-(--screen-w) print:h-(--print-h) print:min-w-0"
        style:--screen-w="{(columnCount + 1) * SCREEN_CELL}px"
        style:--print-h="{cellSize * 7 + 44}px"
        bind:clientWidth={chartWidth}
      >
        <VisibleChart label={`${year} daily chart`} eager={yearIndex === 0}>
          <Chart
            data={chartData}
            x="date"
            c="value"
            cScale={scaleThreshold().unknown("transparent")}
            cDomain={[...glucoseBands]}
            cRange={[
              "var(--glucose-very-low)",
              "var(--glucose-low)",
              "var(--glucose-in-range)",
              "var(--glucose-high)",
              "var(--glucose-very-high)",
            ]}
            tooltipContext={{ mode: "manual" }}
          >
            {#snippet children({ context })}
              <Layer type="svg">
                {#each timeMonths(bounds.start, bounds.end) as monthDate (monthDate.getTime())}
                  <Path
                    pathData={monthOutlinePath(monthDate, bounds.start, firstDay, [cellSize, cellSize])}
                    fill="none"
                    stroke="currentColor"
                    strokeOpacity={0.2}
                  />
                {/each}
                <!-- Month labels (clickable → calendar) -->
                {#each timeMonths(bounds.start, bounds.end) as monthDate (monthDate.getTime())}
                  {@const monthX =
                    layout.columns.find((c) => c.start >= monthDate)?.column ?? 0}
                  <a
                    href={resolve(`calendar?year=${monthDate.getFullYear()}&month=${monthDate.getMonth() + 1}`)}
                  >
                    <text
                      x={monthX * cellSize}
                      y={-5}
                      font-size="12"
                      class="fill-muted-foreground hover:fill-primary cursor-pointer"
                    >
                      {formatMonthLabel(monthDate)}
                    </text>
                  </a>
                {/each}
                <!-- Native <rect> per cell: layerchart marks each call
                     registerMark() on mount and every registration re-runs the
                     chart's mark deriveds over all marks, so ~365 cells/year
                     (x multiple stacked years) cost O(N^2) and stalled the page.
                     Cells carry pre-scaled pixel coords and per-cell handlers,
                     so native <rect> keeps behaviour while registering nothing. -->
                {#each layout.days as day (day.date.getTime())}
                  {@const padding = 1}
                  {@const data = datumByDay.get(toDayString(day.date))}
                  {@const hatch = getCellHatch?.(data)}
                  {@const x = day.column * cellSize + padding}
                  {@const y = day.row * cellSize + padding}
                  <!-- svelte-ignore a11y_click_events_have_key_events -->
                  <!-- svelte-ignore a11y_no_static_element_interactions -->
                  <rect
                    x={x}
                    y={y}
                    width={cellSize - padding * 2}
                    height={cellSize - padding * 2}
                    rx={4}
                    fill={getCellFill(data)}
                    onpointermove={(e: PointerEvent) => context.tooltip?.show(e, data)}
                    onpointerleave={() => context.tooltip?.hide()}
                    onclick={() => {
                      if (data?.dateString) {
                        navigateToDayInReview(data.dateString);
                      }
                    }}
                  ></rect>
                  {#if hatch}
                    <rect
                      x={x}
                      y={y}
                      width={cellSize - padding * 2}
                      height={cellSize - padding * 2}
                      rx={4}
                      fill="none"
                      pointer-events="none"
                      class={patternClass(hatch)}
                    ></rect>
                  {/if}
                {/each}
                {#each layout.columns as column (column.column)}
                  {@const labelX = column.column * cellSize + cellSize / 2}
                  {@const labelY = 7 * cellSize + 10}
                  <a
                    href={resolve(`reports/week-to-week?from=${column.from}&to=${column.to}&isDefault=false`)}
                  >
                    <text
                      x={labelX}
                      y={labelY}
                      text-anchor="start"
                      transform="rotate(45 {labelX} {labelY})"
                      font-size="9"
                      class="fill-muted-foreground hover:fill-primary cursor-pointer"
                    >
                      {formatShortDate(column.start)}
                    </text>
                  </a>
                {/each}
              </Layer>

              <Tooltip.Root
                class="rounded-md border bg-popover p-2.5 text-popover-foreground shadow-md"
              >
                {#snippet children({ data })}
                  {@const d: YearCalendarDatum | undefined = data}
                  {#if d?.dateString}
                    <div class="text-xs min-w-40">
                      <!-- Date header -->
                      <div class="mb-1.5 font-semibold">
                        {formatWeekdayDate(d.date)}
                      </div>

                      <!-- Average glucose -->
                      {#if d.averageGlucoseMgdl != null}
                        <div class="mb-2 flex items-baseline gap-1.5">
                          <span
                            class="text-sm font-bold tabular-nums text-(--avg-color)"
                            style:--avg-color={glucoseColorScale(d.averageGlucoseMgdl)}
                          >
                            {formatGlucoseValue(
                              d.averageGlucoseMgdl,
                              units
                            )}
                          </span>
                          <span class="text-muted-foreground">
                            avg {unitLabel}
                          </span>
                        </div>
                      {/if}

                      <!-- Insulin & Carbs summary -->
                      {#if d.totalDailyDose != null || d.totalCarbs != null}
                        <div
                          class="mb-2 space-y-0.5 border-t border-border/50 pt-1.5"
                        >
                          {#if d.totalDailyDose != null}
                            <div
                              class="text-2xs font-medium text-muted-foreground"
                            >
                              Insulin
                            </div>
                            <div class="flex justify-between gap-4">
                              <span class="text-muted-foreground">
                                Bolus
                              </span>
                              <span class="font-medium tabular-nums">
                                {formatUnits(d.totalBolusUnits)}
                              </span>
                            </div>
                            <div class="flex justify-between gap-4">
                              <span class="text-muted-foreground">
                                Basal
                              </span>
                              <span class="font-medium tabular-nums">
                                {formatUnits(d.totalBasalUnits)}
                              </span>
                            </div>
                            <div
                              class="flex justify-between gap-4 border-t border-border/30 pt-0.5"
                            >
                              <span class="text-muted-foreground">
                                TDD
                              </span>
                              <span class="font-semibold tabular-nums">
                                {formatUnits(d.totalDailyDose)}
                              </span>
                            </div>
                          {/if}
                          {#if d.totalCarbs != null}
                            <div
                              class="flex justify-between gap-4 {d.totalDailyDose !=
                              null
                                ? 'border-t border-border/30 pt-0.5'
                                : ''}"
                            >
                              <span class="text-muted-foreground">
                                Carbs
                              </span>
                              <span class="font-medium tabular-nums">
                                {d.totalCarbs.toFixed(0)}g
                              </span>
                            </div>
                          {/if}
                        </div>
                      {/if}

                      <!-- Record counts -->
                      {#if getVisibleCounts(d.counts).length > 0}
                        {@const visibleCounts = getVisibleCounts(
                          d.counts
                        )}
                        <div
                          class="space-y-0.5 border-t border-border/50 pt-1.5"
                        >
                          <div
                            class="text-2xs font-medium text-muted-foreground"
                          >
                            Counts
                          </div>
                          {#each visibleCounts as [key, count] (key)}
                            <div class="flex justify-between gap-4">
                              <span class="text-muted-foreground">
                                {getDataTypeLabel(key)}
                              </span>
                              <span class="font-medium tabular-nums">
                                {count}
                              </span>
                            </div>
                          {/each}
                        </div>
                      {/if}
                    </div>
                  {/if}
                {/snippet}
              </Tooltip.Root>
            {/snippet}
          </Chart>
        </VisibleChart>
      </div>
    </div>
  {:else if isYearLoading}
    <div
      class="flex h-[120px] items-center justify-center rounded-lg border border-border bg-card"
    >
      <div
        class="flex items-center gap-2 text-sm text-muted-foreground"
      >
        <Loader2 class="h-4 w-4 animate-spin" />
        Loading {year} data...
      </div>
    </div>
  {:else}
    <div
      class="flex h-[120px] items-center justify-center rounded-lg border border-dashed border-border bg-card/50"
    >
      <p class="text-sm text-muted-foreground">
        {days ? `No data for ${year}` : `Scroll to load ${year} data`}
      </p>
    </div>
  {/if}
</div>
