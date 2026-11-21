<script lang="ts">
  import { appChartContext } from "../app-chart-context.svelte";
  import { Chart, Svg } from "layerchart";
  import { scaleTime } from "d3-scale";
  import { IobCobTrack } from "@nocturne/charts";
  import { partialView, setGlucoseChartContext } from "@nocturne/charts";
  import { computeNightscoutTrackLayout, computeTrackLayout } from "@nocturne/charts/core";
  import type { BolusMarkerData, CarbMarkerData, GlucosePoint, SeriesPoint } from "@nocturne/charts";

  interface Props {
    carbMarkers?: CarbMarkerData[];
    bolusMarkers?: BolusMarkerData[];
    glucoseData?: GlucosePoint[];
    variant?: "default" | "nightscout";
    iobData?: SeriesPoint[];
    minIob?: number;
    maxIob?: number;
    onMarkerClick?: (treatmentId: string) => void;
  }

  let {
    carbMarkers = [],
    bolusMarkers = [],
    glucoseData = [],
    variant = "default",
    iobData = [],
    minIob = 0,
    maxIob = 1,
    onMarkerClick,
  }: Props = $props();

  const baseTime = new Date("2026-01-01T12:00:00Z").getTime();
  const height = 300;
  const xDomain: [Date, Date] = [new Date(baseTime), new Date(baseTime + 3 * 60 * 60 * 1000)];

  const engineStub = partialView({
    get iobData() {
      return iobData;
    },
    cobData: [],
    get bolusMarkers() {
      return bolusMarkers;
    },
    get glucoseData() {
      return glucoseData;
    },
    get carbMarkers() {
      return carbMarkers;
    },
  });

  const layout = $derived(
    variant === "nightscout"
      ? computeNightscoutTrackLayout(height, 400, 1)
      : computeTrackLayout(
          height,
          400,
          1,
          maxIob,
          { basal: false, iob: true, cob: true },
          { pumpMode: false, override: false, profile: false, activity: false },
          minIob,
        )
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
</script>

<div class="h-[300px] w-[400px]" data-testid="harness-root">
  <Chart
    data={[{ time: xDomain[0] }, { time: xDomain[1] }]}
    x={(d) => d.time}
    xScale={scaleTime()}
    {xDomain}
    yDomain={[0, 400]}
    padding={{ left: 0, right: 0, top: 0, bottom: 0 }}
  >
    <Svg>
      <IobCobTrack {onMarkerClick} {variant} />
    </Svg>
  </Chart>
</div>
