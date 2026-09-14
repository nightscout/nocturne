<script lang="ts">
  import { Chart, Calendar, Layer, Tooltip } from "layerchart";
  import { scaleThreshold } from "d3-scale";
  import { timeWeek, timeMonths } from "d3-time";
  import { Loader2 } from "lucide-svelte";
  import { fly } from "svelte/transition";
  import { cubicOut } from "svelte/easing";
  import { formatGlucoseValue, formatMonthLabel, formatWeekdayDate } from "$lib/utils/formatting";
  import type { GlucoseUnits } from "$lib/utils/formatting";
  import { getDataTypeLabel } from "$lib/utils/data-type-labels";
  import { yearCalendarBounds } from "./year-bounds";

  let {
    year,
    yearIndex,
    loadingYears,
    yearData,
    transformYearData,
    getCellFill,
    getWeekColumns,
    navigateToDayInReview,
    glucoseColorScale,
    units,
    unitLabel,
    formatUnits,
    getVisibleCounts,
    sentinelElement = $bindable(),
  } = $props<{
    year: number;
    yearIndex: number;
    loadingYears: Set<number>;
    yearData: Map<number, any[]>;
    transformYearData: (days: any[]) => any[];
    getCellFill: (data: any) => string;
    getWeekColumns: (cells: any[]) => any[];
    navigateToDayInReview: (dateStr: string) => void;
    glucoseColorScale: any;
    units: GlucoseUnits;
    unitLabel: string;
    formatUnits: (value: number | null) => string;
    getVisibleCounts: (counts: Record<string, number>) => [string, number][];
    sentinelElement?: HTMLDivElement;
  }>();

  const bounds = $derived(yearCalendarBounds(year));
  const days = $derived(yearData.get(year));
  const chartData = $derived(days ? transformYearData(days) : []);
  const isYearLoading = $derived(loadingYears.has(year) && !days);

  let scrollContainer: HTMLDivElement | undefined = $state();
  let isDragging = $state(false);
  let startX = $state(0);
  let scrollLeftStart = $state(0);
  let hasDragged = $state(false);

  const months = $derived(timeMonths(bounds.start, bounds.end));

  function handleMouseDown(e: MouseEvent) {
    if (!scrollContainer) return;
    isDragging = true;
    hasDragged = false;
    startX = e.pageX - scrollContainer.offsetLeft;
    scrollLeftStart = scrollContainer.scrollLeft;
  }

  function handleMouseMove(e: MouseEvent) {
    if (!isDragging || !scrollContainer) return;
    e.preventDefault();
    const x = e.pageX - scrollContainer.offsetLeft;
    const walk = (x - startX) * 1.5;
    if (Math.abs(walk) > 4) {
      hasDragged = true;
    }
    scrollContainer.scrollLeft = scrollLeftStart - walk;
  }

  function handleMouseUp() {
    isDragging = false;
  }
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
  <div class="mb-2 flex items-center gap-3">
    <h2 class="text-xl font-bold tabular-nums">{year}</h2>
    {#if isYearLoading}
      <Loader2 class="h-4 w-4 animate-spin text-muted-foreground" />
    {/if}
    {#if days}
      <span class="text-sm text-muted-foreground">
        {days.filter((d: any) => (d.totalCount ?? 0) > 0).length} days with data
      </span>
    {/if}
  </div>

  <!-- Calendar Heatmap Card with Enhanced Touch & Grab Scrolling -->
  {#if chartData.length > 0}
                  <rect
                    x={0}
                    y={7 * cellSize[1] + 22}
                    width={1320}
                    height={42}
                    fill="transparent"
                  />
    <div
      bind:this={scrollContainer}
      class="heatmap-scroll-container w-full overflow-x-auto overflow-y-visible rounded-xl border border-border bg-card p-4 print:overflow-visible touch-pan-x cursor-grab active:cursor-grabbing select-none"
      style="-webkit-overflow-scrolling: touch; overscroll-behavior-x: contain;"
      onmousedown={handleMouseDown}
      onmousemove={handleMouseMove}
      onmouseup={handleMouseUp}
      onmouseleave={handleMouseUp}
      role="region"
      aria-label={`${year} heatmap scrollable view`}
    >
      <div class="min-w-[1320px] h-60 pt-2">
        <Chart
          data={chartData}
          x="date"
          c="value"
          cScale={scaleThreshold().unknown("transparent")}
          cDomain={[54, 70, 180, 250]}
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
              <Calendar
                start={bounds.start}
                end={bounds.end}
                cellSize={24}
                monthPath
                monthLabel={false}
              >
                {#snippet children({ cells, cellSize })}
                  <!-- Month labels (clickable → calendar) -->
                  {#each months as monthDate}
                    {@const monthX =
                      timeWeek.count(
                        bounds.start,
                        timeWeek.ceil(monthDate)
                      ) * cellSize[0]}
                    <a
                      href="/calendar?year={monthDate.getFullYear()}&month={monthDate.getMonth() +
                        1}"
                    >
                      <text
                        x={monthX}
                        y={-6}
                        font-size="12"
                        class="fill-muted-foreground hover:fill-primary font-medium cursor-pointer"
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
                  {#each cells as cell}
                    {@const padding = 1}
                    {@const cellDate = cell.data?.dateString}
                    <!-- svelte-ignore a11y_click_events_have_key_events -->
                    <!-- svelte-ignore a11y_no_static_element_interactions -->
                    <rect
                      x={cell.x + padding}
                      y={cell.y + padding}
                      width={cellSize[0] - padding * 2}
                      height={cellSize[1] - padding * 2}
                      rx={4}
                      fill={getCellFill(cell.data)}
                      onpointermove={(e: PointerEvent) => {
                        if (!isDragging) context.tooltip?.show(e, cell.data);
                      }}
                      onpointerleave={() => context.tooltip?.hide()}
                      onclick={() => {
                        if (cellDate && !hasDragged) {
                          navigateToDayInReview(cellDate);
                        }
                      }}
                    />
                  {/each}
                  <!-- Week number labels -->
                  {@const weekCols = getWeekColumns(cells)}
                  {#each weekCols as wk}
                    <a
                      href="/reports/week-to-week?from={wk.from}&to={wk.to}&isDefault=false"
                    >
                      <text
                        x={wk.x + cellSize[0] / 2}
                        y={7 * cellSize[1] + 16}
                        text-anchor="middle"
                        font-size="9"
                        class="fill-muted-foreground hover:fill-primary cursor-pointer"
                      >
                        {wk.weekNumber}
                      </text>
                    </a>
                  {/each}
                {/snippet}
              </Calendar>
            </Layer>

            <Tooltip.Root
              class="rounded-md border bg-popover p-2.5 text-popover-foreground shadow-md"
            >
              {#snippet children({ data })}
                {@const d = data as any}
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
                          class="text-sm font-bold tabular-nums"
                          style="color: {glucoseColorScale(
                            d.averageGlucoseMgdl
                          )}"
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
                            class="text-[10px] font-medium uppercase tracking-wider text-muted-foreground"
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
                          class="text-[10px] font-medium uppercase tracking-wider text-muted-foreground"
                        >
                          Counts
                        </div>
                        {#each visibleCounts as [key, count]}
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
      </div>
      <div class="min-w-[1320px] h-10" aria-hidden="true"></div>
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
        No data for {year}
      </p>
    </div>
  {/if}
</div>
