<script lang="ts">
  import {
    Chart,
    Svg,
    Area,
    Spline,
    Axis,
    Text,
    BrushContext,
    ChartClipPath,
  } from "layerchart";
  import { scaleTime, scaleLinear } from "d3-scale";
  import { curveMonotoneX } from "d3";
  import type { BrushContextValue } from "layerchart";
  import RotateCcw from "lucide-svelte/icons/rotate-ccw";

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
  }: Props = $props();

  // Reserved for future threshold coloring
  void _highThreshold;
  void _lowThreshold;

  // Track brush context for handle labels
  let brushContext = $state<BrushContextValue | undefined>(undefined);

  // Whether we have an active selection (zoomed in)
  const hasSelection = $derived(
    selectedXDomain !== null &&
      (selectedXDomain[0].getTime() !== fullXDomain[0].getTime() ||
        selectedXDomain[1].getTime() !== fullXDomain[1].getTime())
  );

  // Format time for handle labels
  function formatTime(date: Date): string {
    return date.toLocaleTimeString([], {
      hour: "numeric",
      minute: "2-digit",
    });
  }

  // Format date for longer displays
  function formatDateTime(date: Date): string {
    const now = new Date();
    const isToday = date.toDateString() === now.toDateString();

    if (isToday) {
      return formatTime(date);
    }

    return date.toLocaleDateString([], {
      month: "short",
      day: "numeric",
      hour: "numeric",
      minute: "2-digit",
    });
  }

  // Handle brush end - update selection
  function handleBrushEnd(e: { xDomain: unknown; yDomain: unknown }) {
    if (e.xDomain && Array.isArray(e.xDomain) && e.xDomain.length === 2) {
      const newDomain: [Date, Date] = [
        new Date(e.xDomain[0] as number),
        new Date(e.xDomain[1] as number),
      ];
      selectedXDomain = newDomain;
      onSelectionChange?.(newDomain);
    }
  }

  // Handle brush change for live updates
  function handleBrushChange(e: { xDomain: unknown; yDomain: unknown }) {
    if (e.xDomain && Array.isArray(e.xDomain) && e.xDomain.length === 2) {
      const newDomain: [Date, Date] = [
        new Date(e.xDomain[0] as number),
        new Date(e.xDomain[1] as number),
      ];
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

<div class="mini-overview-chart">
  <!-- Header with reset button -->
  <div
    class="w-full flex items-center justify-between px-3 py-1.5 text-xs text-muted-foreground"
  >
    <span class="font-medium">Full Range Overview</span>
    {#if hasSelection}
      <button
        type="button"
        class="flex items-center gap-1 px-2 py-0.5 text-[10px] bg-primary/10 text-primary rounded hover:bg-primary/20 transition-colors"
        onclick={resetSelection}
      >
        <RotateCcw size={10} />
        Reset zoom
      </button>
    {/if}
  </div>

  <!-- Mini chart -->
  {#if expanded}
    <div class="h-[80px] px-2 pb-2">
      <Chart
        {data}
        x={(d: GlucosePoint) => d.time}
        y="sgv"
        xScale={scaleTime()}
        xDomain={[fullXDomain[0], fullXDomain[1]]}
        yScale={scaleLinear()}
        {yDomain}
        padding={{ left: 48, bottom: 20, top: 4, right: 48 }}
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
              format={(v) => (v instanceof Date ? formatTime(v) : String(v))}
              tickLabelProps={{ class: "text-[9px] fill-muted-foreground" }}
            />
          </Svg>

          <!-- Brush context for selection -->
          <BrushContext
            bind:brushContext
            axis="x"
            mode="separated"
            xDomain={selectedXDomain ?? fullXDomain}
            onBrushEnd={handleBrushEnd}
            onChange={handleBrushChange}
            classes={{
              range: "bg-primary/20 border border-primary/40 rounded",
              handle: "bg-primary/60 hover:bg-primary/80 rounded-sm",
            }}
          >
            {#snippet children({ brushContext: bc })}
              <!-- Handle labels showing time values -->
              {#if bc.isActive && bc.xDomain}
                <!-- Left handle label -->
                <div
                  class="absolute text-[9px] font-medium text-primary bg-background/90 px-1 py-0.5 rounded shadow-sm border border-border whitespace-nowrap pointer-events-none z-20"
                  style="left: {bc.range.x + bc.range.width / 2 - 2}px; top: {bc
                    .range.y - 18}px; transform: translateX(-100%)"
                >
                  {formatDateTime(new Date(bc.xDomain[0] as number))}
                </div>

                <!-- Right handle label -->
                <div
                  class="absolute text-[9px] font-medium text-primary bg-background/90 px-1 py-0.5 rounded shadow-sm border border-border whitespace-nowrap pointer-events-none z-20"
                  style="left: {bc.range.x + bc.range.width + 2}px; top: {bc
                    .range.y - 18}px;"
                >
                  {formatDateTime(new Date(bc.xDomain[1] as number))}
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
