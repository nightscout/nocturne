<script lang="ts">
  import { Chart, Svg } from "layerchart";
  import { scaleTime } from "d3-scale";
  import IobCobTrack from "./IobCobTrack.svelte";
  import { partialEngine, setGlucoseChartContext } from "../chart-context.svelte";
  import { computeTrackLayout } from "../engine/track-layout";
  import type { GlucoseChartContext } from "../chart-context.svelte";
  import type { CarbMarkerData, SeriesPoint } from "../engine/chart-data-view.svelte";

  interface Props {
    carbMarkers?: CarbMarkerData[];
    iobData?: SeriesPoint[];
    minIob?: number;
    maxIob?: number;
    onMarkerClick?: (treatmentId: string) => void;
  }

  let { carbMarkers = [], iobData = [], minIob = 0, maxIob = 1, onMarkerClick }: Props = $props();

  const baseTime = new Date("2026-01-01T12:00:00Z").getTime();
  const height = 300;
  const xDomain: [Date, Date] = [new Date(baseTime), new Date(baseTime + 3 * 60 * 60 * 1000)];

  const engineStub = partialEngine({
    get iobData() {
      return iobData;
    },
    cobData: [],
    bolusMarkers: [],
    get carbMarkers() {
      return carbMarkers;
    },
  });

  const layout = $derived(
    computeTrackLayout(
      height,
      400,
      1,
      maxIob,
      { basal: false, iob: true, cob: true },
      { pumpMode: false, override: false, profile: false, activity: false },
      minIob,
    )
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
      <IobCobTrack {onMarkerClick} />
    </Svg>
  </Chart>
</div>
