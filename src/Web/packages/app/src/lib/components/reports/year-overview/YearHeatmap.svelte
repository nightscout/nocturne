<script lang="ts">
  import { Chart, Calendar, Layer, Rect, Tooltip } from "layerchart";
  import { scaleThreshold } from "d3-scale";
  import { timeWeek, timeMonths } from "d3-time";
  import { Loader2 } from "lucide-svelte";
  import { fly } from "svelte/transition";
  import { cubicOut } from "svelte/easing";
  import { formatGlucoseValue } from "$lib/utils/formatting";
  import type { GlucoseUnits } from "$lib/utils/formatting";
  import { getDataTypeLabel } from "$lib/utils/data-type-labels";

  let {
    year,
    yearIndex,
    loadingYears,
    yearData,
    getYearBounds,
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
    getYearBounds: (year: number) => { start: Date; end: Date };
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

  const bounds = $derived(getYearBounds(year));
  const days = $derived(yearData.get(year));
  const chartData = $derived(days ? transformYearData(days) : []);
  const isYearLoading = $derived(loadingYears.has(year) && !days);
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
  <div class="mb-3 flex items-center gap-3">
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

  <!-- Calendar Heatmap -->
  {#if chartData.length > 0}
    <div
      class="w-full overflow-x-auto overflow-y-visible rounded-lg border border-border bg-card p-4 print:overflow-visible"
    >
      <div class="min-w-[900px] h-60">
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
          tooltip={{ mode: "manual" }}
        >
          {#snippet children({ context })}
            <Layer type="svg">
              <Calendar
                start={bounds.start}
                end={bounds.end}
                cellSize={24}
                monthPath
                monthLabel={false}
                tooltipContext={context.tooltip}
              >
                {#snippet children({ cells, cellSize })}
                  <!-- Month labels (clickable → calendar) -->
                  {#each timeMonths(bounds.start, bounds.end) as monthDate}
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
                        y={-5}
                        font-size="12"
                        class="fill-muted-foreground hover:fill-primary cursor-pointer"
                      >
                        {monthDate.toLocaleString(undefined, {
                          month: "short",
                        })}
                      </text>
                    </a>
                  {/each}
                  {#each cells as cell}
                    {@const padding = 1}
                    {@const cellDate = cell.data?.dateString}
                    <Rect
                      x={cell.x + padding}
                      y={cell.y + padding}
                      width={cellSize[0] - padding * 2}
                      height={cellSize[1] - padding * 2}
                      rx={4}
                      fill={getCellFill(cell.data)}
                      onpointermove={(e) =>
                        context.tooltip?.show(e, cell.data)}
                      onpointerleave={() => context.tooltip?.hide()}
                      onclick={() => {
                        if (cellDate) {
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
                        y={7 * cellSize[1] + 14}
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
                      {d.date.toLocaleDateString(undefined, {
                        weekday: "short",
                        month: "short",
                        day: "numeric",
                      })}
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
