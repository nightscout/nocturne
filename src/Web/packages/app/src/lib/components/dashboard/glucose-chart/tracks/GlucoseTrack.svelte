<script lang="ts">
  import { Spline, Points, Rule, Axis, ChartClipPath, Highlight } from "layerchart";
  import { curveMonotoneX } from "d3";
  import type { ScaleLinear } from "d3-scale";
  import PredictionVisualizations from "../../PredictionVisualizations.svelte";
  import type { PredictionData } from "$lib/data/predictions.remote";
  import type { PredictionDisplayMode } from "$lib/stores/appearance-store.svelte";

  interface GlucoseDataPoint {
    time: Date;
    sgv: number;
    color: string;
  }

  interface Props {
    glucoseData: GlucoseDataPoint[];
    glucoseScale: ScaleLinear<number, number>;
    glucoseAxisScale: ScaleLinear<number, number>;
    glucoseTrackTop: number;
    highThreshold: number;
    lowThreshold: number;
    contextWidth: number;
    // Prediction props
    showPredictions: boolean;
    predictionData: PredictionData | null;
    predictionEnabled: boolean;
    predictionDisplayMode: PredictionDisplayMode;
    predictionError: string | null;
    chartXDomain: { from: Date; to: Date };
  }

  let {
    glucoseData,
    glucoseScale,
    glucoseAxisScale,
    glucoseTrackTop,
    highThreshold,
    lowThreshold,
    contextWidth,
    showPredictions,
    predictionData,
    predictionEnabled,
    predictionDisplayMode,
    predictionError,
    chartXDomain,
  }: Props = $props();

  // Only show points when density is reasonable (less than 0.5 points per pixel)
  const pointDensity = $derived(glucoseData.length / contextWidth);
  const showGlucosePoints = $derived(pointDensity < 0.5);
</script>

<!-- High threshold line -->
<Rule y={glucoseScale(highThreshold)} class="stroke-glucose-high/50" stroke-dasharray="4,4" />

<!-- Low threshold line -->
<Rule y={glucoseScale(lowThreshold)} class="stroke-glucose-very-low/50" stroke-dasharray="4,4" />

<!-- Glucose axis on left -->
<Axis
  placement="left"
  scale={glucoseAxisScale}
  ticks={5}
  tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
/>

<ChartClipPath>
  <!-- Glucose line -->
  <Spline
    data={glucoseData}
    x={(d) => d.time}
    y={(d) => glucoseScale(d.sgv)}
    class="stroke-glucose-in-range stroke-2 fill-none"
    motion="spring"
    curve={curveMonotoneX}
  />

  <!-- Glucose points -->
  {#if showGlucosePoints}
    {#each glucoseData as point}
      <Points
        data={[point]}
        x={(d) => d.time}
        y={(d) => glucoseScale(d.sgv)}
        r={3}
        fill={point.color}
        class="opacity-90"
      />
    {/each}
  {/if}

  <!-- Prediction visualizations -->
  <PredictionVisualizations
    {showPredictions}
    {predictionData}
    {predictionEnabled}
    {predictionDisplayMode}
    {predictionError}
    {glucoseScale}
    {glucoseTrackTop}
    {chartXDomain}
    {glucoseData}
  />
</ChartClipPath>

<!-- Glucose highlight (main) -->
<ChartClipPath>
  <Highlight
    x={(d) => d.time}
    y={(d) => glucoseScale(d.sgv)}
    points
    lines
  />
</ChartClipPath>
