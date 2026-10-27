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
    getGlucoseColor,
    getGlucoseColorContinuous,
  } from "#lib/utils/chart-colors.js";
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
    visibleOnly = false,
  }: Props = $props();

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  const glucoseData = $derived(ctx.engine.glucoseData);
  const bisectTime = bisector((d: GlucosePoint) => d.time);
  const drawnData = $derived.by(() => {
    if (!visibleOnly) return glucoseData;
    const [from, to] = chartCtx.xScale.domain();
    const start = Math.max(0, bisectTime.left(glucoseData, from) - 2);
    return glucoseData.slice(start, bisectTime.right(glucoseData, to) + 2);
  });
  const glucoseScale = $derived(ctx.layout.glucose.scale);
  const glucoseAxisScale = $derived(ctx.layout.glucose.axisScale);
  const thresholds = $derived(ctx.engine.thresholds);

  // Only show points when density is reasonable (less than 0.5 points per pixel)
  const pointDensity = $derived(glucoseData.length / chartCtx.width);
  const effectiveShowPoints = $derived(showPoints ?? pointDensity < 0.5);

  const effectivePointMode = $derived(
    pointColorMode ?? (pointColor !== undefined ? "single" : lineColorMode),
  );
  const effectivePointColor = $derived(pointColor ?? lineColor);

  const y0 = $derived(
    areaMode === "off" ? undefined : areaY0Accessor(areaMode, thresholds),
  );

  const lineStops = $derived(
    lineStopsFor(lineColorMode, thresholds, glucoseAxisScale, chartCtx.height),
  );

  const fillStops = $derived(
    fillStopsFor(areaMode, lineStops, lineColor, areaOpacity),
  );

  function pointFill(sgv: number): string {
    if (effectivePointMode === "single") return effectivePointColor;
    if (effectivePointMode === "continuous")
      return getGlucoseColorContinuous(sgv);
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
      data={drawnData}
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
          data={drawnData}
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
</ChartClipPath>
