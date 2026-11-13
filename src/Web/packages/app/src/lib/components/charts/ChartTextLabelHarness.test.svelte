<script lang="ts">
  import { appChartContext } from "#lib/components/dashboard/glucose-chart/app-chart-context.svelte.js";
  import { glucoseChartLabels } from "#lib/components/dashboard/glucose-chart/chart-labels.svelte.js";
  import { Chart, Svg } from "layerchart";
  import { scaleTime } from "d3-scale";
  import BasalRateTrack from "./BasalRateTrack.svelte";
  import { IobCobTrack } from "@nocturne/charts";
  import { PredictionVisualizations } from "@nocturne/charts";
  import { partialView, setGlucoseChartContext } from "@nocturne/charts";
  import { computeTrackLayout } from "@nocturne/charts/core";
  import type { PredictionData } from "#lib/api/predictions.remote.js";

  interface Props {
    track: "basalRate" | "iobCob" | "predictions";
    width?: number;
    height?: number;
    predictionError?: string | null;
    /** Throwing here trips the component's error boundary. */
    glucoseScale?: (v: number) => number;
    predictionData?: PredictionData | null;
  }

  let {
    track,
    width = 600,
    height = 300,
    predictionError = null,
    glucoseScale = (v: number) => v,
    predictionData = null,
  }: Props = $props();

  const baseTime = new Date("2026-01-01T00:00:00Z").getTime();
  const stepMs = 5 * 60 * 1000;
  const at = (i: number) => new Date(baseTime + i * stepMs);

  const glucoseYMax = 400;
  const maxBasalRate = 3;

  const series = Array.from({ length: 6 }, (_, i) => ({
    time: at(i),
    value: 1 + (i % 3) * 0.5,
  }));

  const engineStub = partialView({
    get iobData() {
      return series;
    },
    get cobData() {
      return series;
    },
    get bolusMarkers() {
      return [];
    },
    get carbMarkers() {
      return [];
    },
  });

  const layout = $derived(
    computeTrackLayout(
      height,
      glucoseYMax,
      maxBasalRate,
      1,
      { basal: true, iob: true, cob: true },
      { pumpMode: false, override: false, profile: false, activity: false },
    ),
  );

  const ctx = appChartContext({
    get view() {
      return engineStub;
    },
    get layout() {
      return layout;
    },
  });
  setGlucoseChartContext(ctx);

  const xDomain = $derived<[Date, Date]>([at(0), at(5)]);

  const basalTrackContext = {
    xScale: (d: Date) => (d.getTime() - baseTime) / stepMs,
    yScale: (v: number) => v,
  };
</script>

<div class="h-(--harness-h) w-(--harness-w)" style:--harness-w="{width}px" style:--harness-h="{height}px" data-testid="harness-root">
  <Chart
    data={series}
    x={(d: (typeof series)[number]) => d.time}
    y={(d: (typeof series)[number]) => d.value}
    xScale={scaleTime()}
    {xDomain}
    yDomain={[0, glucoseYMax]}
    padding={{ left: 0, right: 0, top: 0, bottom: 0 }}
  >
    <Svg>
      {#if track === "basalRate"}
        <BasalRateTrack
          {maxBasalRate}
          trackHeight={80}
          trackTop={20}
          chartHeight={height}
          {glucoseYMax}
          context={basalTrackContext}
          showAxis={false}
        />
      {:else if track === "iobCob"}
        <IobCobTrack />
      {:else}
        <PredictionVisualizations
          showPredictions={true}
          {predictionData}
          predictionEnabled={true}
          predictionDisplayMode="lines"
          {predictionError}
          {glucoseScale}
          glucoseTrackTop={20}
          chartXDomain={{ from: at(0), to: at(5) }}
          glucoseData={series.map((d) => ({ time: d.time, sgv: 120 }))}
          labels={glucoseChartLabels()}
        />
      {/if}
    </Svg>
  </Chart>
</div>
