<script lang="ts">
  import {
    Spline,
    Area,
    Circle,
    Axis,
    LinearGradient,
    ChartClipPath,
    getChartContext,
  } from "layerchart";
  import { bisector, curveMonotoneX } from "d3";
  import { getGlucoseChartContext } from "../chart-context.svelte";
  import type { GlucosePoint } from "../engine/chart-data-view.svelte";
  import { bg } from "#lib/utils/formatting.js";
  import {
    distinctRangeStops,
    getGlucoseColor,
    getGlucoseColorContinuous,
  } from "#lib/utils/chart-colors.js";
  import {
    glucoseRangePalette,
    type GlucoseRangePalette,
  } from "#lib/stores/appearance-store.svelte.js";
  import {
    lineStopsFor,
    fillStopsFor,
    areaY0Accessor,
    type AreaMode,
    type LineColorMode,
  } from "../utils/glucose-gradient-stops";

  /**
   * GlucoseTrack renders the glucose line/area/points layer of a chart.
   *
   * Customisation is composable across four feature axes:
   *   - `lineColorMode` (default `"continuous"`): line strokes along the
   *     oklch glucose spectrum unless overridden to `"threshold"` (one colour
   *     per bucket: very-low/low/in-range/high/very-high) or `"single"`
   *     (flat `lineColor`).
   *   - `areaMode` (default `"off"`): adds a filled area below the line
   *     extending to the chart bottom, at flat opacity (`"full"`) or fading
   *     out toward the bottom (`"baseline"`), or only where the line
   *     deviates outside `[low, high]` (`"deviation"`).
   *   - Points (`showPoints` / `pointColorMode` / `pointColor`): mirror
   *     the line by default, but can diverge. Passing `pointColor` alone
   *     (without `pointColorMode`) implicitly switches points to
   *     `"single"` mode using that colour.
   *   - `areaOpacity` (default 0.5): top of the vertical opacity fade;
   *     the fill always fades to transparent at the chart bottom.
   */
  interface Props {
    showAxis?: boolean;
    showPoints?: boolean;
    areaMode?: AreaMode;
    lineColorMode?: LineColorMode;
    lineColor?: string;
    pointColorMode?: LineColorMode;
    pointColor?: string;
    areaOpacity?: number;
    /** The range palette the continuous colours follow; the viewer's own when omitted. A server render has no viewer store, so it passes the request's. */
    rangePalette?: GlucoseRangePalette;
    /**
     * Draw only the readings in the chart's x domain. Two more on each side keep the curve and
     * the points at its edges as the whole series draws them: a monotone curve's segment reads
     * the readings either side of it.
     */
    visibleOnly?: boolean;
  }

  let {
    showAxis = true,
    showPoints,
    areaMode = "off",
    lineColorMode = "continuous",
    lineColor = "var(--glucose-in-range)",
    pointColorMode,
    pointColor,
    areaOpacity = 0.5,
    rangePalette,
    visibleOnly = false,
  }: Props = $props();

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  // The line runs through every reading, so a sensor error reads as one stretch of the trace;
  // the invalid ones are outlined on top of it.
  const lineData = $derived(ctx.engine.allGlucoseData);
  const glucoseData = $derived(ctx.engine.glucoseData);
  const invalidData = $derived(ctx.engine.invalidGlucoseData);
  const bisectTime = bisector((d: GlucosePoint) => d.time);
  function inView(series: GlucosePoint[]): GlucosePoint[] {
    if (!visibleOnly) return series;
    const [from, to] = chartCtx.xScale.domain();
    const start = Math.max(0, bisectTime.left(series, from) - 2);
    return series.slice(start, bisectTime.right(series, to) + 2);
  }
  const drawnLine = $derived(inView(lineData));
  const drawnData = $derived(inView(glucoseData));
  const drawnInvalid = $derived(inView(invalidData));
  const glucoseScale = $derived(ctx.layout.glucose.scale);
  const glucoseAxisScale = $derived(ctx.layout.glucose.axisScale);
  const thresholds = $derived(ctx.engine.thresholds);

  // Only show points when density is reasonable (less than 0.5 points per pixel)
  const pointDensity = $derived(lineData.length / chartCtx.width);
  const effectiveShowPoints = $derived(showPoints ?? pointDensity < 0.5);

  const effectivePointMode = $derived(
    pointColorMode ?? (pointColor !== undefined ? "single" : lineColorMode),
  );
  const effectivePointColor = $derived(pointColor ?? lineColor);

  const y0 = $derived(
    areaMode === "off" ? undefined : areaY0Accessor(areaMode, thresholds),
  );

  const rangeStops = $derived(
    distinctRangeStops(
      rangePalette ?? glucoseRangePalette.current,
      thresholds,
    ),
  );

  const lineStops = $derived(
    lineStopsFor(
      lineColorMode,
      thresholds,
      glucoseAxisScale,
      chartCtx.height,
      rangeStops,
    ),
  );

  const fillStops = $derived(
    fillStopsFor(areaMode, lineStops, lineColor, areaOpacity),
  );

  function pointFill(sgv: number): string {
    if (effectivePointMode === "single") return effectivePointColor;
    if (effectivePointMode === "continuous")
      return getGlucoseColorContinuous(sgv, rangeStops);
    return getGlucoseColor(sgv, thresholds);
  }

  // A tick on the track's top edge prints level with the swim lane's gutter label.
  const printTicks = $derived.by(() => {
    const [, top] = glucoseAxisScale.domain();
    return glucoseAxisScale.ticks(5).filter((v) => v < top);
  });
</script>

{#if showAxis}
  <Axis
    placement="left"
    scale={glucoseAxisScale}
    ticks={ctx.printing ? printTicks : 5}
    format={(v) => String(bg(v, ctx.glucoseUnits))}
    tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
  />
{/if}

{#snippet splineOrArea(stroke: string)}
  {#if areaMode === "off"}
    <Spline
      data={drawnLine}
      x={(d) => d.time}
      y={(d) => glucoseScale(d.sgv)}
      {stroke}
      class="stroke-2 fill-none"
      motion="spring"
      curve={curveMonotoneX}
    />
  {:else}
    <LinearGradient
      stops={fillStops}
      units="userSpaceOnUse"
      vertical
      y1="0"
      y2={String(chartCtx.height)}
    >
      {#snippet children({ gradient: fillGrad })}
        <Area
          data={drawnLine}
          x={(d: GlucosePoint) => d.time}
          y1={(d: GlucosePoint) => glucoseScale(d.sgv)}
          {y0}
          line={{ stroke, class: "stroke-2" }}
          fill={fillGrad}
          curve={curveMonotoneX}
        />
      {/snippet}
    </LinearGradient>
  {/if}
{/snippet}

<ChartClipPath>
  {#if lineColorMode === "single"}
    {@render splineOrArea(lineColor)}
  {:else}
    <!-- A percentage extent resolves against the whole SVG, padding included,
         so pin it to the plot area the stops were computed against. -->
    <LinearGradient
      stops={lineStops}
      units="userSpaceOnUse"
      vertical
      y1="0"
      y2={String(chartCtx.height)}
    >
      {#snippet children({ gradient: strokeGrad })}
        {@render splineOrArea(strokeGrad)}
      {/snippet}
    </LinearGradient>
  {/if}

  {#if effectiveShowPoints}
    <!-- Single data-mode Circle instead of one <Points> per reading: layerchart
         marks each register with the chart on mount and every registration
         re-runs the chart's mark-dependent deriveds over all marks, so N points
         cost O(N^2) synchronous work. Data mode renders the whole series from
         one mark with a per-point fill accessor. -->
    <Circle
      data={drawnData}
      key={(d) => d.time.getTime()}
      cx={(d) => d.time}
      cy={(d) => glucoseScale(d.sgv)}
      r={3}
      fill={(d) => pointFill(d.sgv)}
      class="opacity-90"
    />
  {/if}

  {#if drawnInvalid.length > 0}
    <!-- Drawn even with points off, and outlined in the foreground colour like a finger stick,
         so these readings are the ones the eye goes to. -->
    <Circle
      data={drawnInvalid}
      key={(d) => d.time.getTime()}
      cx={(d) => d.time}
      cy={(d) => glucoseScale(d.sgv)}
      r={3}
      fill={(d) => pointFill(d.sgv)}
      class="stroke-foreground [stroke-width:1]"
    />
  {/if}
</ChartClipPath>
