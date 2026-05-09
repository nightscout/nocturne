<script lang="ts">
  import {
    Spline,
    Area,
    Points,
    Axis,
    LinearGradient,
    ChartClipPath,
    getChartContext,
  } from "layerchart";
  import { curveMonotoneX } from "d3";
  import { getGlucoseChartContext } from "../chart-context.svelte";
  import { bg } from "$lib/utils/formatting";
  import {
    getGlucoseColor,
    getGlucoseColorContinuous,
  } from "$lib/utils/chart-colors";
  import {
    thresholdLineStops,
    continuousLineStops,
    fillStopsFromLineStops,
    singleColorFillStops,
    areaY0Accessor,
  } from "../utils/glucose-gradient-stops";

  type LineColorMode = "single" | "threshold" | "continuous";
  type AreaMode = "off" | "baseline" | "deviation";

  /**
   * GlucoseTrack renders the glucose line/area/points layer of a chart.
   *
   * Customisation is composable across four feature axes:
   *   - `lineColorMode` (default `"threshold"`): line strokes by glucose
   *     bucket (very-low/low/in-range/high/very-high) unless overridden to
   *     `"single"` (flat `lineColor`) or `"continuous"` (oklch spectrum).
   *     The threshold default applies to every consumer that doesn't pass
   *     the prop explicitly; callers wanting the previous in-range green
   *     line must opt in via `lineColorMode="single"`.
   *   - `areaMode` (default `"off"`): adds a filled area below the line
   *     extending to the chart bottom (`"baseline"`) or only where the
   *     line deviates outside `[low, high]` (`"deviation"`).
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
  }

  let {
    showAxis = true,
    showPoints,
    areaMode = "off",
    lineColorMode = "threshold",
    lineColor = "var(--glucose-in-range)",
    pointColorMode,
    pointColor,
    areaOpacity = 0.5,
  }: Props = $props();

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  const glucoseData = $derived(ctx.engine.glucoseData);
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

  const lineStops = $derived.by(() => {
    if (lineColorMode === "single") return undefined;
    if (lineColorMode === "threshold")
      return thresholdLineStops(thresholds, glucoseAxisScale, chartCtx.height);
    return continuousLineStops(glucoseAxisScale, chartCtx.height);
  });

  const fillStops = $derived.by(() => {
    if (areaMode === "off") return undefined;
    if (lineColorMode === "single")
      return singleColorFillStops(lineColor, areaOpacity);
    return lineStops ? fillStopsFromLineStops(lineStops, areaOpacity) : undefined;
  });

  function pointFill(sgv: number): string {
    if (effectivePointMode === "single") return effectivePointColor;
    if (effectivePointMode === "continuous")
      return getGlucoseColorContinuous(sgv);
    return getGlucoseColor(sgv, thresholds);
  }
</script>

{#if showAxis}
  <Axis
    placement="left"
    scale={glucoseAxisScale}
    ticks={5}
    format={(v) => String(bg(v))}
    tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
  />
{/if}

<ChartClipPath>
  {#snippet splineOrArea(stroke: string)}
    {#if areaMode === "off"}
      <Spline
        data={glucoseData}
        x={(d) => d.time}
        y={(d) => glucoseScale(d.sgv)}
        {stroke}
        class="stroke-2 fill-none"
        motion="spring"
        curve={curveMonotoneX}
      />
    {:else}
      <LinearGradient stops={fillStops} units="userSpaceOnUse" vertical>
        {#snippet children({ gradient: fillGrad })}
          <Area
            data={glucoseData}
            x={(d) => d.time}
            y={(d) => glucoseScale(d.sgv)}
            {y0}
            line={{ stroke, class: "stroke-2" }}
            fill={fillGrad}
            curve={curveMonotoneX}
          />
        {/snippet}
      </LinearGradient>
    {/if}
  {/snippet}

  {#if lineColorMode === "single"}
    {@render splineOrArea(lineColor)}
  {:else}
    <LinearGradient stops={lineStops} units="userSpaceOnUse" vertical>
      {#snippet children({ gradient: strokeGrad })}
        {@render splineOrArea(strokeGrad)}
      {/snippet}
    </LinearGradient>
  {/if}

  {#if effectiveShowPoints}
    {#each glucoseData as point (point.time.getTime())}
      <Points
        data={[point]}
        x={(d) => d.time}
        y={(d) => glucoseScale(d.sgv)}
        r={3}
        fill={pointFill(point.sgv)}
        class="opacity-90"
      />
    {/each}
  {/if}
</ChartClipPath>
