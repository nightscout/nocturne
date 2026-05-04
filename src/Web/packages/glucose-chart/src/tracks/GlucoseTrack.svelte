<script lang="ts">
  import { Spline, Points, Rule, Axis, ChartClipPath, Highlight } from "layerchart";
  import { bisector, curveMonotoneX } from "d3";
  import type { ScaleLinear } from "d3-scale";
  import { bg } from "../utils/formatting.js";

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
    onPointClick?: (data: GlucoseDataPoint) => void;
    heartRateSeries?: Array<{ time: Date; bpm: number }>;
    stepSeries?: Array<{ time: Date; steps: number }>;
  }

  let {
    glucoseData,
    glucoseScale,
    glucoseAxisScale,
    glucoseTrackTop,
    highThreshold,
    lowThreshold,
    contextWidth,
    onPointClick,
    heartRateSeries = [],
    stepSeries = [],
  }: Props = $props();

  // Only show points when density is reasonable (less than 0.5 points per pixel)
  const pointDensity = $derived(glucoseData.length / contextWidth);
  const showGlucosePoints = $derived(pointDensity < 0.5);

  // Heartrate normalization range
  const MIN_BPM = 50;
  const MAX_BPM = 180;

  // Normalize heartrate BPM to glucose Y scale:
  // MIN_BPM -> low threshold, MAX_BPM -> high threshold
  const heartRateToGlucose = (bpm: number) => {
    return lowThreshold + ((bpm - MIN_BPM) / (MAX_BPM - MIN_BPM)) * (highThreshold - lowThreshold);
  };

  // Pre-compute step bubble positions: Y = 2-hour trailing glucose average
  const TWO_HOURS_MS = 2 * 60 * 60 * 1000;
  const MAX_STEPS = 500;
  const MIN_RADIUS = 2;
  const MAX_RADIUS = 8;

  const bisectTime = bisector((d: GlucoseDataPoint) => d.time.getTime()).left;

  const stepBubbles = $derived(
    stepSeries.map((step) => {
      const stepMs = step.time.getTime();
      const cutoff = stepMs - TWO_HOURS_MS;
      const startIdx = bisectTime(glucoseData, cutoff);
      const endIdx = bisectTime(glucoseData, stepMs + 1);
      const window = glucoseData.slice(startIdx, endIdx);
      const avgSgv =
        window.length > 0
          ? window.reduce((sum, g) => sum + g.sgv, 0) / window.length
          : (lowThreshold + highThreshold) / 2;
      const radius = MIN_RADIUS + (Math.min(step.steps, MAX_STEPS) / MAX_STEPS) * (MAX_RADIUS - MIN_RADIUS);
      return { time: step.time, sgv: avgSgv, radius };
    })
  );
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
  format={(v) => String(bg(v))}
  tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
/>

<!-- Heartrate line (behind glucose) -->
{#if heartRateSeries.length > 0}
<ChartClipPath>
  <Spline
    data={heartRateSeries}
    x={(d) => d.time}
    y={(d) => glucoseScale(heartRateToGlucose(d.bpm))}
    class="fill-none"
    style="stroke: var(--heart-rate); opacity: 0.3; stroke-width: 1.5px;"
    curve={curveMonotoneX}
  />
</ChartClipPath>
{/if}

<!-- Step bubbles (behind glucose) -->
{#if stepBubbles.length > 0}
<ChartClipPath>
  {#each stepBubbles as bubble}
    <Points
      data={[bubble]}
      x={(d) => d.time}
      y={(d) => glucoseScale(d.sgv)}
      r={bubble.radius}
      class="stroke-none"
      style="fill: var(--steps); opacity: 0.25;"
    />
  {/each}
</ChartClipPath>
{/if}

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
</ChartClipPath>

<!-- Glucose highlight (main) -->
<ChartClipPath>
  <Highlight
    x={(d) => d.time}
    y={(d) => glucoseScale(d.sgv)}
    points
    lines
    onPointClick={onPointClick
      ? (_e, { data }) => onPointClick(data)
      : undefined}
  />
</ChartClipPath>
