<script lang="ts">
  import {
    Chart,
    Svg,
    Spline,
    Rule,
    Circle,
    Text,
    Axis,
    ChartClipPath,
  } from "layerchart";
  import { scaleTime, scaleLinear } from "d3-scale";
  import { curveMonotoneX } from "d3";
  import type { PredictionData } from "$api/predictions.remote";
  import { bg } from "$lib/utils/formatting";

  interface GlucoseDataPoint {
    time: Date;
    sgv: number;
    color: string;
  }

  interface Props {
    glucoseData: GlucoseDataPoint[];
    centerTime: Date;
    predictionData?: PredictionData | null;
    highThreshold: number;
    lowThreshold: number;
    label?: string;
  }

  let {
    glucoseData,
    centerTime,
    predictionData = null,
    highThreshold,
    lowThreshold,
    label,
  }: Props = $props();

  // Compute x domain from glucose data
  const xDomain = $derived.by((): [Date, Date] => {
    if (glucoseData.length === 0) return [new Date(), new Date()];
    const times = glucoseData.map((d) => d.time.getTime());
    return [new Date(Math.min(...times)), new Date(Math.max(...times))];
  });

  // Filter prediction curves to only points within the x domain
  const predictionCurveData = $derived.by(() => {
    if (!predictionData?.curves.main) return [];
    const xMin = xDomain[0].getTime();
    const xMax = xDomain[1].getTime();
    return predictionData.curves.main
      .filter((p) => p.timestamp >= xMin && p.timestamp <= xMax)
      .map((p) => ({
        time: new Date(p.timestamp),
        sgv: p.value,
      }));
  });

  // Compute y domain: max of glucose values, prediction values, and thresholds, with 10% headroom
  const yDomain = $derived.by((): [number, number] => {
    const glucoseValues = glucoseData.map((d) => d.sgv);
    const predValues = predictionCurveData.map((d) => d.sgv);
    const allValues = [...glucoseValues, ...predValues, highThreshold];
    const maxVal = allValues.length > 0 ? Math.max(...allValues) : 300;
    const yMax = Math.ceil(maxVal * 1.1);
    return [0, yMax];
  });

  // Find peak point (highest glucose value)
  const peakPoint = $derived.by(() => {
    if (glucoseData.length === 0) return null;
    return glucoseData.reduce((max, d) => (d.sgv > max.sgv ? d : max));
  });

  // Find nadir point (lowest glucose value)
  const nadirPoint = $derived.by(() => {
    if (glucoseData.length === 0) return null;
    return glucoseData.reduce((min, d) => (d.sgv < min.sgv ? d : min));
  });

  // Only show nadir annotation if it differs from peak
  const showNadir = $derived(
    peakPoint !== null &&
      nadirPoint !== null &&
      peakPoint.time.getTime() !== nadirPoint.time.getTime()
  );
</script>

<div class="h-[200px] w-full">
  <Chart
    data={glucoseData}
    x={(d: GlucoseDataPoint) => d.time}
    y="sgv"
    xScale={scaleTime()}
    xDomain={[xDomain[0], xDomain[1]]}
    yScale={scaleLinear()}
    yDomain={[yDomain[0], yDomain[1]]}
    padding={{ left: 48, bottom: 24, top: 16, right: 16 }}
  >
    {#snippet children()}
      <Svg>
        <!-- High threshold line -->
        <Rule
          y={highThreshold}
          class="stroke-glucose-high/40"
          stroke-dasharray="4,4"
        />

        <!-- Low threshold line -->
        <Rule
          y={lowThreshold}
          class="stroke-glucose-very-low/40"
          stroke-dasharray="4,4"
        />

        <!-- Center time vertical rule -->
        <Rule
          x={centerTime}
          class="stroke-muted-foreground/60"
          stroke-dasharray="6,3"
        />

        <!-- Center time label -->
        {#if label}
          <Text
            x={centerTime.getTime()}
            y={yDomain[1]}
            dy={-4}
            textAnchor="middle"
            class="text-[9px] fill-muted-foreground"
          >
            {label}
          </Text>
        {/if}

        <ChartClipPath>
          <!-- Glucose line -->
          <Spline
            data={glucoseData}
            x={(d: GlucoseDataPoint) => d.time}
            y="sgv"
            class="stroke-glucose-in-range stroke-2 fill-none"
            curve={curveMonotoneX}
          />

          <!-- Glucose points: one data-mode Circle for the whole series (single
               mark registration) instead of one <Points> per reading (O(N^2)). -->
          <Circle
            data={glucoseData}
            key={(d: GlucoseDataPoint) => d.time.getTime()}
            cx={(d: GlucoseDataPoint) => d.time}
            cy="sgv"
            r={3}
            fill={(d: GlucoseDataPoint) => d.color}
            class="opacity-90"
          />

          <!-- Prediction curves (main only for mini chart) -->
          {#if predictionData && predictionCurveData.length > 0}
            <Spline
              data={predictionCurveData}
              x={(d: { time: Date; sgv: number }) => d.time}
              y="sgv"
              curve={curveMonotoneX}
              class="stroke-purple-400/60 stroke-1 fill-none"
              stroke-dasharray="4,2"
            />
          {/if}
        </ChartClipPath>

        <!-- Peak annotation -->
        {#if peakPoint}
          <Text
            x={peakPoint.time.getTime()}
            y={peakPoint.sgv}
            dy={-10}
            textAnchor="middle"
            class="text-[9px] fill-foreground font-medium"
          >
            {bg(peakPoint.sgv)}
          </Text>
        {/if}

        <!-- Nadir annotation (only if different from peak) -->
        {#if showNadir && nadirPoint}
          <Text
            x={nadirPoint.time.getTime()}
            y={nadirPoint.sgv}
            dy={14}
            textAnchor="middle"
            class="text-[9px] fill-foreground font-medium"
          >
            {bg(nadirPoint.sgv)}
          </Text>
        {/if}

        <!-- Left Y-axis with glucose values -->
        <Axis
          placement="left"
          ticks={4}
          format={(v) => String(bg(v as number))}
          tickLabelProps={{ class: "text-[10px] fill-muted-foreground" }}
        />

        <!-- Bottom X-axis with time labels -->
        <Axis
          placement="bottom"
          ticks={4}
          format={(v) =>
            v instanceof Date
              ? v.toLocaleTimeString([], {
                  hour: "numeric",
                  minute: "2-digit",
                })
              : String(v)}
          tickLabelProps={{ class: "text-[9px] fill-muted-foreground" }}
        />
      </Svg>
    {/snippet}
  </Chart>
</div>
