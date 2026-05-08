<script lang="ts">
  import type { Snippet } from "svelte";
  import { Chart, Svg, Axis, BrushContext } from "layerchart";
  import { scaleTime } from "d3-scale";
  import type { ChartDataEngine } from "./engine/chart-data-engine.svelte";
  import type { PointInspection } from "./engine/point-inspection.svelte";
  import type { GlucoseChartContext, LegendState } from "./chart-context.svelte";
  import { setGlucoseChartContext } from "./chart-context.svelte";
  import { computeTrackLayout } from "./engine/track-layout";

  interface Props {
    engine: ChartDataEngine;
    inspection?: PointInspection;
    legend?: LegendState;
    brushDomain?: [Date, Date] | null;
    heightClass?: string;
    padding?: { left: number; right: number; top: number; bottom: number };
    selectionDomain?: [Date, Date] | null;
    onSelectionChange?: (domain: [Date, Date] | null) => void;
    showTimeAxis?: boolean;
    tracks: Snippet<[GlucoseChartContext]>;
    /** HTML overlays rendered after Svg but inside Chart (tooltips, etc.) */
    overlays?: Snippet<[GlucoseChartContext]>;
  }

  const {
    engine,
    inspection,
    legend,
    brushDomain,
    heightClass = "h-full",
    padding = { left: 48, bottom: 30, top: 8, right: 48 },
    selectionDomain,
    onSelectionChange,
    showTimeAxis = true,
    tracks,
    overlays,
  }: Props = $props();

  let chartHeight = $state(0);
  let chartWidth = $state(0);

  const layout = $derived(
    computeTrackLayout(
      chartHeight,
      engine.glucoseYMax,
      engine.maxBasalRate,
      engine.maxIOB,
      {
        basal: legend?.basal ?? true,
        iob: legend?.iob ?? true,
        cob: legend?.cob ?? true,
      },
      {
        pumpMode:
          (legend?.pumpModes ?? true) &&
          engine.displayPumpModeSpans.length > 0,
        override:
          (legend?.overrideSpans ?? false) &&
          engine.displayOverrideSpans.length > 0,
        profile:
          (legend?.profileSpans ?? false) &&
          engine.displayProfileSpans.length > 0,
        activity:
          (legend?.activitySpans ?? false) &&
          engine.displayActivitySpans.length > 0,
      }
    )
  );

  const chartXDomain = $derived({
    from: brushDomain?.[0] ?? engine.displayDateRange.from,
    to: brushDomain?.[1] ?? engine.displayDateRangeWithPredictions.to,
  });

  const ctx: GlucoseChartContext = {
    get engine() {
      return engine;
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
  };
  setGlucoseChartContext(ctx);
</script>

<div class="{heightClass} w-full @container">
  <Chart
    data={engine.glucoseData}
    x={(d) => d.time}
    y="sgv"
    xScale={scaleTime()}
    xDomain={[chartXDomain.from, chartXDomain.to]}
    yDomain={[0, engine.glucoseYMax]}
    {padding}
    tooltip={{ mode: "quadtree-x" }}
  >
    {#snippet children({ context })}
      {(chartHeight = context.height, chartWidth = context.width, "")}

      <Svg>
        {#if chartHeight > 0}
          {@render tracks(ctx)}
        {/if}

        {#if showTimeAxis}
          <Axis
            placement="bottom"
            format="hour"
            tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
          />
        {/if}
      </Svg>

      {#if chartHeight > 0}
        {@render overlays?.(ctx)}
      {/if}

      {#if onSelectionChange}
        <BrushContext
          axis="x"
          mode="separated"
          xDomain={selectionDomain ?? [chartXDomain.from, chartXDomain.to]}
          onChange={(e) => {
            if (
              e.xDomain &&
              Array.isArray(e.xDomain) &&
              e.xDomain.length === 2
            ) {
              onSelectionChange?.([
                new Date(e.xDomain[0]),
                new Date(e.xDomain[1]),
              ]);
            }
          }}
          classes={{
            range: "bg-warning/30 border border-warning/60 rounded",
            handle: "bg-warning hover:bg-warning/80 rounded-sm",
          }}
        />
      {/if}
    {/snippet}
  </Chart>
</div>
