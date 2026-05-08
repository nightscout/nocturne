<script lang="ts">
  import { Chart, Svg } from "layerchart";
  import { scaleTime } from "d3-scale";
  import GlucoseTrack from "./GlucoseTrack.svelte";
  import { setGlucoseChartContext } from "../chart-context.svelte";
  import { computeTrackLayout } from "../engine/track-layout";
  import type { GlucoseChartContext } from "../chart-context.svelte";
  import type {
    ChartDataEngine,
    GlucosePoint,
  } from "../engine/chart-data-engine.svelte";

  interface Props {
    glucoseData?: GlucosePoint[];
    showAxis?: boolean;
    showPoints?: boolean;
    areaMode?: "off" | "baseline" | "deviation";
    lineColorMode?: "single" | "threshold" | "continuous";
    lineColor?: string;
    pointColorMode?: "single" | "threshold" | "continuous";
    pointColor?: string;
    areaOpacity?: number;
    width?: number;
    height?: number;
  }

  const baseTime = new Date("2026-01-01T12:00:00Z").getTime();
  const defaultData: GlucosePoint[] = [
    { time: new Date(baseTime), sgv: 60, color: "var(--glucose-low)" },
    {
      time: new Date(baseTime + 5 * 60 * 1000),
      sgv: 120,
      color: "var(--glucose-in-range)",
    },
    {
      time: new Date(baseTime + 10 * 60 * 1000),
      sgv: 220,
      color: "var(--glucose-high)",
    },
  ];

  let {
    glucoseData = defaultData,
    // Default to false so test assertions over the rendered DOM don't have to
    // account for the axis tick `<text>`/`<line>` elements. Component default
    // is true; tests opt back in by passing showAxis explicitly.
    showAxis = false,
    showPoints,
    areaMode = "off",
    lineColorMode = "threshold",
    lineColor,
    pointColorMode,
    pointColor,
    areaOpacity,
    width = 400,
    height = 300,
  }: Props = $props();

  const thresholds = {
    low: 70,
    high: 180,
    veryLow: 55,
    veryHigh: 250,
    glucoseYMax: 400,
  };

  // Minimal stub of ChartDataEngine — GlucoseTrack only reads `glucoseData`
  // and `thresholds` from the engine (other fields like `width`/`height` come
  // from layerchart's own context, not the engine). The `Partial<...> as ...`
  // cast is intentional: if GlucoseTrack starts touching a new engine field,
  // it should light up as a type error here rather than as runtime undefined.
  const engineStub = {
    get glucoseData() {
      return glucoseData;
    },
    thresholds,
  } as Partial<ChartDataEngine> as ChartDataEngine;

  const layout = $derived(
    computeTrackLayout(
      height,
      thresholds.glucoseYMax,
      1,
      1,
      { basal: false, iob: false, cob: false },
      { pumpMode: false, override: false, profile: false, activity: false },
    ),
  );

  const ctx: GlucoseChartContext = {
    get engine() {
      return engineStub;
    },
    get layout() {
      return layout;
    },
  };
  setGlucoseChartContext(ctx);

  const xDomain = $derived<[Date, Date]>([
    glucoseData[0]?.time ?? new Date(baseTime),
    glucoseData[glucoseData.length - 1]?.time ?? new Date(baseTime + 1),
  ]);
</script>

<div style="width: {width}px; height: {height}px;" data-testid="harness-root">
  <Chart
    data={glucoseData}
    x={(d) => d.time}
    y="sgv"
    xScale={scaleTime()}
    {xDomain}
    yDomain={[0, thresholds.glucoseYMax]}
    padding={{ left: 0, right: 0, top: 0, bottom: 0 }}
  >
    <Svg>
      <GlucoseTrack
        {showAxis}
        {showPoints}
        {areaMode}
        {lineColorMode}
        lineColor={lineColor ?? "var(--glucose-in-range)"}
        {pointColorMode}
        {pointColor}
        areaOpacity={areaOpacity ?? 0.5}
      />
    </Svg>
  </Chart>
</div>
