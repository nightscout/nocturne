<script lang="ts">
  import {
    Chart,
    Svg,
    Area,
    Spline,
    Axis,
    BrushContext,
    ChartClipPath,
    LinearGradient,
    Circle,
  } from "layerchart";
  import { scaleTime, scaleLinear, scaleLog, type ScaleContinuousNumeric } from "d3-scale";
  import { tv } from "tailwind-variants";
  import { curveMonotoneX } from "d3";
  import RotateCcw from "@lucide/svelte/icons/rotate-ccw";
  import { Button } from "#lib/components/ui/button/index.js";
  import {
    time,
    formatDateTimeCompact,
    formatWeekdayDate,
    hourLabel,
  } from "#lib/utils/formatting.js";
  import {
    distinctRangeStops,
    getGlucoseColor,
    type GlucoseThresholds,
  } from "@nocturne/charts/core";
  import { glucoseRangePalette } from "#lib/stores/appearance-store.svelte.js";
  import {
    lineStopsFor,
    fillStopsFor,
    type AreaMode,
    type LineColorMode,
  } from "@nocturne/charts/core";

  interface GlucosePoint {
    time: Date;
    sgv: number;
    color: string;
  }

  interface PredictionPoint {
    time: Date;
    value: number;
  }

  interface Props {
    /** Glucose data for the mini chart */
    data: GlucosePoint[];
    /** Full x domain (the entire time range) */
    fullXDomain: [Date, Date];
    /** Current selected x domain (bindable) */
    selectedXDomain?: [Date, Date] | null;
    /** Y domain for glucose values */
    yDomain: [number, number];
    /** Whether the mini chart is expanded */
    expanded?: boolean;
    /** Callback when selection changes */
    onSelectionChange?: (xDomain: [Date, Date] | null) => void;
    /** Prediction data to display */
    predictionData?: PredictionPoint[] | null;
    /** Whether to show predictions */
    showPredictions?: boolean;
    /**
     * Whether the main chart is zoomed by a brush. Governs the reset button;
     * when omitted, any selection narrower than the full domain counts.
     */
    isZoomed?: boolean;
    /**
     * Plot gutters. Keep `left`/`right` equal to the main chart's so the brush
     * lines up with it. `top` must hold the range label: 18px plus a gap.
     */
    padding?: { left: number; right: number; top: number; bottom: number };
    /** Mirrors the main chart's glucose line style. */
    thresholds: GlucoseThresholds;
    lineColorMode?: LineColorMode;
    lineColor?: string;
    areaMode?: AreaMode;
    areaOpacity?: number;
    /**
     * "nightscout" draws cgm-remote-monitor's context chart: each reading as a 2px dot on
     * a log scale (36-420 mg/dL), half-faded outside the selection, the selection outlined,
     * and no header, line or forecast.
     */
    variant?: "default" | "nightscout";
  }

  let {
    data,
    fullXDomain,
    selectedXDomain = $bindable(null),
    yDomain,
    expanded = $bindable(true),
    onSelectionChange,
    predictionData = null,
    showPredictions = false,
    isZoomed,
    padding = { left: 48, right: 48, top: 20, bottom: 20 },
    thresholds,
    lineColorMode = "continuous",
    lineColor = "var(--glucose-in-range)",
    areaMode = "off",
    areaOpacity = 0.5,
    variant = "default",
  }: Props = $props();

  const nightscout = $derived(variant === "nightscout");
  const yScale = $derived<ScaleContinuousNumeric<number, number>>(
    nightscout ? scaleLog().clamp(true) : scaleLinear()
  );
  /** chart.js: the context chart's log domain. */
  const NIGHTSCOUT_CONTEXT_DOMAIN: [number, number] = [36, 420];

  const overview = tv({
    slots: {
      root: "print:hidden",
      plot: "px-2 pb-2",
      range: "bg-primary/20 border border-primary/40 rounded",
      handle: "bg-primary/60 hover:bg-primary/80 rounded-sm",
      axisLabel: "text-2xs fill-muted-foreground",
    },
    variants: {
      variant: {
        default: { plot: "h-[96px]" },
        nightscout: {
          plot: "h-[var(--ns-context-height,180px)] px-0 pb-0",
          range: "bg-transparent border-x-2 border-t-2 border-b-0 border-white rounded-none",
          handle: "bg-transparent hover:bg-transparent",
          axisLabel: "fill-foreground",
        },
      },
    },
  });
  const styles = $derived(overview({ variant }));

  function inSelection(d: GlucosePoint): boolean {
    const [from, to] = selectedXDomain ?? fullXDomain;
    const t = d.time.getTime();
    return t >= from.getTime() && t <= to.getTime();
  }

  // Past midnight a tick names the day, as Nightscout's context axis does.
  function nightscoutTickLabel(v: unknown): string {
    if (!(v instanceof Date)) return String(v);
    return v.getHours() === 0 && v.getMinutes() === 0 ? formatWeekdayDate(v) : hourLabel(v);
  }

  /** Widest range label at this font, used to keep it inside the strip. */
  const RANGE_LABEL_HALF_WIDTH = 60;

  // Whether we have an active selection (zoomed in)
  const hasSelection = $derived(
    selectedXDomain !== null &&
      (selectedXDomain[0].getTime() !== fullXDomain[0].getTime() ||
        selectedXDomain[1].getTime() !== fullXDomain[1].getTime())
  );
  // The dashboard always hands us a selection (its viewing window), so left to
  // hasSelection the reset button would never go away.
  const showReset = $derived(isZoomed ?? hasSelection);

  // Format date for longer displays
  function formatDateTime(date: Date): string {
    const now = new Date();
    const isToday = date.toDateString() === now.toDateString();
    if (isToday) {
      return time(date);
    }
    return formatDateTimeCompact(date);
  }

  // layerchart 2.x brush events carry the BrushState; its x domain holds the
  // current selection. Used for both live updates (onChange) and brush end.
  function handleBrush(e: { brush: { x: Array<number | Date | string | null> } }) {
    const [start, end] = e.brush.x ?? [];
    if (start != null && end != null) {
      const newDomain: [Date, Date] = [new Date(start), new Date(end)];
      selectedXDomain = newDomain;
      onSelectionChange?.(newDomain);
    }
  }

  // Reset selection to full range
  function resetSelection() {
    selectedXDomain = null;
    onSelectionChange?.(null);
  }
</script>

<div class={styles.root()} class:mini-overview-chart={!nightscout}>
  {#if !nightscout}
  <!-- Header with reset button -->
  <div
    class="w-full flex items-center justify-between px-3 py-1.5 text-xs text-muted-foreground"
  >
    <span class="font-medium">Full range overview</span>
    {#if showReset}
      <Button variant="link" size="xs" onclick={resetSelection}>
        <RotateCcw size={10} />
        Reset zoom
      </Button>
    {/if}
  </div>
  {/if}

  <!-- Mini chart -->
  {#if expanded}
    <!-- Tall enough for the range label to sit in the top gutter above the plot. -->
    <div class={styles.plot()}>
      <Chart
        {data}
        x={(d: GlucosePoint) => d.time}
        y="sgv"
        xScale={scaleTime()}
        xDomain={[fullXDomain[0], fullXDomain[1]]}
        {yScale}
        yDomain={nightscout ? NIGHTSCOUT_CONTEXT_DOMAIN : yDomain}
        {padding}
      >
        {#snippet children({ context })}
          {@const lineStops = lineStopsFor(
            lineColorMode,
            thresholds,
            context.yScale,
            context.height,
            distinctRangeStops(glucoseRangePalette.current, thresholds)
          )}
          {@const fillStops = fillStopsFor(
            areaMode,
            lineStops,
            lineColor,
            areaOpacity
          )}
          <Svg>
            {#if nightscout}
              {#each [thresholds.high, thresholds.low] as level (level)}
                <line
                  x1={0}
                  x2={context.width}
                  y1={context.yScale(level)}
                  y2={context.yScale(level)}
                  class="stroke-(--ns-rule-target)"
                  stroke-dasharray="3,3"
                  shape-rendering="crispEdges"
                />
              {/each}
              <ChartClipPath>
                <Circle
                  {data}
                  key={(d) => d.time.getTime()}
                  cx={(d) => d.time}
                  cy={(d) => d.sgv}
                  r={2}
                  fill={(d) => getGlucoseColor(d.sgv, thresholds)}
                  fillOpacity={(d) => (inSelection(d) ? 1 : 0.5)}
                />
              </ChartClipPath>
            {:else}
            <ChartClipPath>
              {#if fillStops}
                <LinearGradient
                  stops={fillStops}
                  units="userSpaceOnUse"
                  vertical
                  y1="0"
                  y2={String(context.height)}
                >
                  {#snippet children({ gradient })}
                    <Area
                      {data}
                      x={(d: GlucosePoint) => d.time}
                      y="sgv"
                      y0={() => yDomain[0]}
                      curve={curveMonotoneX}
                      fill={gradient}
                    />
                  {/snippet}
                </LinearGradient>
              {/if}

              {#if lineStops}
                <LinearGradient
                  stops={lineStops}
                  units="userSpaceOnUse"
                  vertical
                  y1="0"
                  y2={String(context.height)}
                >
                  {#snippet children({ gradient })}
                    <Spline
                      {data}
                      x={(d: GlucosePoint) => d.time}
                      y="sgv"
                      curve={curveMonotoneX}
                      stroke={gradient}
                      class="stroke-1 fill-none"
                    />
                  {/snippet}
                </LinearGradient>
              {:else}
                <Spline
                  {data}
                  x={(d: GlucosePoint) => d.time}
                  y="sgv"
                  curve={curveMonotoneX}
                  stroke={lineColor}
                  class="stroke-1 fill-none"
                />
              {/if}

              <!-- Prediction line -->
              {#if showPredictions && predictionData && predictionData.length > 0}
                <Spline
                  data={predictionData}
                  x={(d: PredictionPoint) => d.time}
                  y={(d: PredictionPoint) => d.value}
                  curve={curveMonotoneX}
                  class="stroke-primary/60 stroke-1 fill-none"
                  stroke-dasharray="3,3"
                />
              {/if}
            </ChartClipPath>
            {/if}
            <!-- X axis -->
            <Axis
              placement="bottom"
              ticks={nightscout ? 6 : 4}
              format={(v) =>
                nightscout ? nightscoutTickLabel(v) : v instanceof Date ? time(v) : String(v)}
              tickOcclusion={nightscout}
              tickLabelProps={nightscout
                ? { class: styles.axisLabel(), fontSize: 16 }
                : { class: styles.axisLabel() }}
            />
          </Svg>

          <!-- Brush context for selection -->
          <BrushContext
            axis="x"
            x={selectedXDomain ?? fullXDomain}
            onBrushEnd={handleBrush}
            onChange={handleBrush}
            classes={{ range: styles.range(), handle: styles.handle() }}
          >
            {#snippet children({ state: bc })}
              <!-- One label for the selected range, centred over the brush.
                     This snippet renders in a container anchored at the chart's
                     outer corner, while bc.range is measured from the plot's,
                     so add the padding back or the label lands a gutter to the
                     left and above the strip. Clamped to stay inside the strip
                     when the brush hugs an edge, as it does at "now". -->
              {#if !nightscout && bc.active && bc.x?.[0] != null && bc.x?.[1] != null}
                <div
                  class="absolute text-2xs font-medium text-primary bg-background/90 px-1 py-0.5 rounded shadow-sm border border-border whitespace-nowrap pointer-events-none z-20 left-(--label-left) top-(--label-top) -translate-x-1/2"
                  style:--label-left="clamp({RANGE_LABEL_HALF_WIDTH}px, {padding.left +
                    bc.range.x +
                    bc.range.width / 2}px, calc(100% - {RANGE_LABEL_HALF_WIDTH}px))"
                  style:--label-top="{padding.top + bc.range.y - 18}px"
                >
                  {formatDateTime(new Date(bc.x[0]))} - {formatDateTime(
                    new Date(bc.x[1])
                  )}
                </div>
              {/if}
            {/snippet}
          </BrushContext>
        {/snippet}
      </Chart>
    </div>

    <!-- Selection info -->
    {#if !nightscout && hasSelection && selectedXDomain}
      <div
        class="flex items-center justify-center gap-2 px-3 py-1 text-2xs text-muted-foreground border-t border-border"
      >
        <span>Viewing:</span>
        <span class="font-medium text-foreground">
          {formatDateTime(selectedXDomain[0])} - {formatDateTime(
            selectedXDomain[1]
          )}
        </span>
        <span class="text-muted-foreground/70">
          ({Math.round(
            (selectedXDomain[1].getTime() - selectedXDomain[0].getTime()) /
              (1000 * 60)
          )} min)
        </span>
      </div>
    {/if}
  {/if}
</div>

<style>
  .mini-overview-chart {
    border-top: 1px solid var(--border);
    background: color-mix(in oklch, var(--muted) 30%, transparent);
    border-radius: 0 0 var(--radius) var(--radius);
  }
</style>
