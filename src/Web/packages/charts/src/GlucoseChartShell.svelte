<script lang="ts">
  import type { Snippet } from "svelte";
  import { Chart, Svg, Axis, BrushContext } from "layerchart";
  import { scaleTime } from "d3-scale";
  import type { ChartDataView } from "./view/chart-data-view.svelte.js";
  import type {
    ChartInspector,
    GlucoseChartContext,
    LegendState,
  } from "./context.svelte.js";
  import { setGlucoseChartContext } from "./context.svelte.js";
  import { computeTrackLayout } from "./view/track-layout.js";
  import {
    formatHourLabel,
    formatWeekdayDate,
    type ChartFormat,
    type GlucoseUnits,
  } from "./format.js";
  import type { ChartLabels } from "./labels.js";
  import { hourTicks } from "./view/axis-ticks.js";

  interface Props {
    view: ChartDataView;
    inspection?: ChartInspector;
    legend?: LegendState;
    brushDomain?: [Date, Date] | null;
    heightClass?: string;
    padding?: { left: number; right: number; top: number; bottom: number };
    selectionDomain?: [Date, Date] | null;
    onSelectionChange?: (domain: [Date, Date] | null) => void;
    showTimeAxis?: boolean;
    glucoseUnits: GlucoseUnits;
    labels: ChartLabels;
    format: ChartFormat;
    /** Lay the chart out for paper; see {@link GlucoseChartContext.printing}. */
    printing?: boolean;
    tracks: Snippet<[GlucoseChartContext]>;
    /** HTML overlays rendered after Svg but inside Chart (tooltips, etc.) */
    overlays?: Snippet<[GlucoseChartContext]>;
    /** Draws at this size instead of measuring its box, which lets the server render it. */
    size?: { width: number; height: number };
  }

  const {
    view,
    inspection,
    legend,
    brushDomain,
    heightClass = "h-full",
    padding = { left: 48, bottom: 30, top: 8, right: 48 },
    selectionDomain,
    onSelectionChange,
    showTimeAxis = true,
    glucoseUnits,
    labels,
    format,
    printing = false,
    tracks,
    overlays,
    size,
  }: Props = $props();

  let chartHeight = $state(0);

  const layout = $derived(
    computeTrackLayout(
      chartHeight,
      view.thresholds.glucoseYMax,
      view.maxBasalRate,
      view.maxIOB,
      {
        basal: legend?.basal ?? true,
        iob: legend?.iob ?? true,
        cob: legend?.cob ?? true,
      },
      {
        pumpMode:
          (legend?.pumpModes ?? true) &&
          view.displayPumpModeSpans.length > 0,
        override:
          (legend?.overrideSpans ?? false) &&
          view.displayOverrideSpans.length > 0,
        profile:
          (legend?.profileSpans ?? false) &&
          view.displayProfileSpans.length > 0,
        activity:
          (legend?.activitySpans ?? false) &&
          view.displayActivitySpans.length > 0,
      },
      view.minIOB
    )
  );

  // Paper carries no forecast, so its axis ends where the period does.
  const chartXDomain = $derived({
    from: brushDomain?.[0] ?? view.displayDateRange.from,
    to:
      brushDomain?.[1] ??
      (printing ? view.displayDateRange.to : view.displayDateRangeWithPredictions.to),
  });

  // Past a day and a half the ticks land on midnights, which read as a row of
  // identical "12 AM"s unless they name the day.
  const multiDay = $derived(
    chartXDomain.to.getTime() - chartXDomain.from.getTime() > 36 * 60 * 60 * 1000
  );

  function tickLabel(v: unknown): string {
    if (!(v instanceof Date)) return String(v);
    const midnight = v.getHours() === 0 && v.getMinutes() === 0;
    return multiDay && midnight ? formatWeekdayDate(v, format) : formatHourLabel(v, format);
  }

  const ctx: GlucoseChartContext = {
    get view() {
      return view;
    },
    get layout() {
      return layout;
    },
    get inspection() {
      return inspection;
    },
    get legend() {
      return legend;
    },
    get printing() {
      return printing;
    },
    get glucoseUnits() {
      return glucoseUnits;
    },
    get labels() {
      return labels;
    },
    get format() {
      return format;
    },
  };
  setGlucoseChartContext(ctx);
</script>

<!-- print:break-inside-auto lets a tall multi-day chart flow across pages instead of being clipped by the global break-inside:avoid on svg. -->
<div class="{heightClass} w-full @container print:break-inside-auto">
  <Chart
    data={view.glucoseData}
    x={(d) => d.time}
    y="sgv"
    xScale={scaleTime()}
    xDomain={[chartXDomain.from, chartXDomain.to]}
    yDomain={[0, view.thresholds.glucoseYMax]}
    {padding}
    tooltipContext={{ mode: "quadtree-x" }}
    ssr={size !== undefined}
    width={size?.width}
    height={size?.height}
  >
    {#snippet children({ context })}
      {(chartHeight = context.height, "")}

      <Svg>
        {#if chartHeight > 0}
          {@render tracks(ctx)}
        {/if}

        {#if showTimeAxis}
          <Axis
            placement="bottom"
            ticks={hourTicks}
            format={tickLabel}
            tickOcclusion
            tickLabelProps={{ class: "text-xs fill-muted-foreground", fontSize: 12 }}
          />
        {/if}
      </Svg>

      {#if chartHeight > 0}
        {@render overlays?.(ctx)}
      {/if}

      {#if view.chartDataError}
        <div class="pointer-events-none absolute inset-0 flex items-center justify-center p-4">
          <p role="alert" class="text-center text-sm text-destructive">
            {view.chartDataError}
          </p>
        </div>
      {/if}

      {#if onSelectionChange}
        <BrushContext
          axis="x"
          x={selectionDomain ?? [chartXDomain.from, chartXDomain.to]}
          onChange={(e) => {
            const [start, end] = e.brush.x ?? [];
            if (start != null && end != null) {
              onSelectionChange?.([new Date(start), new Date(end)]);
            }
          }}
          classes={{
            range: "bg-warning/30 border border-warning/60 rounded print:border-dashed print:border-foreground",
            handle: "bg-warning hover:bg-warning/80 rounded-sm print:hidden",
          }}
        />
      {/if}
    {/snippet}
  </Chart>
</div>
