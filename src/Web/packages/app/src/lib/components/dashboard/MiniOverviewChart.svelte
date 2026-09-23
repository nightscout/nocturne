<script lang="ts">
  import {
    Chart,
    Svg,
    Area,
    Spline,
    Axis,
    BrushContext,
    ChartClipPath,
  } from "layerchart";
  import { scaleTime, scaleLinear } from "d3-scale";
  import { curveMonotoneX } from "d3";
  import RotateCcw from "lucide-svelte/icons/rotate-ccw";
  import { Button } from "$lib/components/ui/button";
  import { time, formatDateTimeCompact } from "$lib/utils/formatting";

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
    /** High threshold for coloring (reserved for future use) */
    highThreshold?: number;
    /** Low threshold for coloring (reserved for future use) */
    lowThreshold?: number;
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
  }

  let {
    data,
    fullXDomain,
    selectedXDomain = $bindable(null),
    yDomain,
    expanded = $bindable(true),
    onSelectionChange,
    highThreshold: _highThreshold = 180,
    lowThreshold: _lowThreshold = 70,
    predictionData = null,
    showPredictions = false,
    isZoomed,
    padding = { left: 48, right: 48, top: 20, bottom: 20 },
  }: Props = $props();

  /** Widest range label at this font, used to keep it inside the strip. */
  const RANGE_LABEL_HALF_WIDTH = 60;

  // Reserved for future threshold coloring
  // svelte-ignore state_referenced_locally
  void _highThreshold;
  // svelte-ignore state_referenced_locally
  void _lowThreshold;

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

<div class="mini-overview-chart print:hidden">
  <!-- Header with reset button -->
  <div
    class="w-full flex items-center justify-between px-3 py-1.5 text-xs text-muted-foreground"
  >
    <span class="font-medium">Full Range Overview</span>
    {#if showReset}
      <Button variant="link" size="xs" onclick={resetSelection}>
        <RotateCcw size={10} />
        Reset zoom
      </Button>
    {/if}
  </div>

  <!-- Mini chart -->
  {#if expanded}
    <!-- Tall enough for the range label to sit in the top gutter above the plot. -->
    <div class="h-[96px] px-2 pb-2">
      <Chart
        {data}
        x={(d: GlucosePoint) => d.time}
        y="sgv"
        xScale={scaleTime()}
        xDomain={[fullXDomain[0], fullXDomain[1]]}
        yScale={scaleLinear()}
        {yDomain}
        {padding}
      >
        {#snippet children()}
          <Svg>
            <ChartClipPath>
              <!-- Glucose area fill -->
              <Area
                {data}
                x={(d: GlucosePoint) => d.time}
                y="sgv"
                y0={() => yDomain[0]}
                curve={curveMonotoneX}
                fill="var(--glucose-in-range)"
                class="opacity-20"
              />

              <!-- Glucose line -->
              <Spline
                {data}
                x={(d: GlucosePoint) => d.time}
                y="sgv"
                curve={curveMonotoneX}
                class="stroke-glucose-in-range stroke-1 fill-none"
              />

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
            <!-- X axis -->
            <Axis
              placement="bottom"
              ticks={4}
              format={(v) => (v instanceof Date ? time(v) : String(v))}
              tickLabelProps={{ class: "text-[9px] fill-muted-foreground" }}
            />
          </Svg>

          <!-- Brush context for selection -->
          <BrushContext
            axis="x"
            x={selectedXDomain ?? fullXDomain}
            onBrushEnd={handleBrush}
            onChange={handleBrush}
            classes={{
              range: "bg-primary/20 border border-primary/40 rounded",
              handle: "bg-primary/60 hover:bg-primary/80 rounded-sm",
            }}
          >
            {#snippet children({ state: bc })}
              <!-- One label for the selected range, centred over the brush.
                   This snippet renders in a container anchored at the chart's
                   outer corner, while bc.range is measured from the plot's,
                   so add the padding back or the label lands a gutter to the
                   left and above the strip. Clamped to stay inside the strip
                   when the brush hugs an edge, as it does at "now". -->
              {#if bc.active && bc.x?.[0] != null && bc.x?.[1] != null}
                <div
                  class="absolute text-[9px] font-medium text-primary bg-background/90 px-1 py-0.5 rounded shadow-sm border border-border whitespace-nowrap pointer-events-none z-20 left-(--label-left) top-(--label-top) -translate-x-1/2"
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
    {#if hasSelection && selectedXDomain}
      <div
        class="flex items-center justify-center gap-2 px-3 py-1 text-[10px] text-muted-foreground border-t border-border"
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
    border-top: 1px solid hsl(var(--border));
    background: hsl(var(--muted) / 0.3);
    border-radius: 0 0 var(--radius) var(--radius);
  }
</style>
