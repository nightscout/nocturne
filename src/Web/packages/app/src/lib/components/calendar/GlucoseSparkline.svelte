<script lang="ts">
  /**
   * Tiny glucose curve for embedding in calendar day cells. Uses the shared
   * GlucoseTrack component with threshold-coloured line and deviation-fill
   * area, so the curve picks up the same visual contract as the dashboard chart
   * (red below low, in-range with no fill, red above high).
   *
   * Pure visual — no button, no click handling — so callers can wrap it in
   * whatever interactive element they need without nested-button HTML.
   */
  import { Chart, Svg } from "layerchart";
  import { scaleTime } from "d3-scale";
  import {
    partialEngine,
    setGlucoseChartContext,
  } from "#lib/components/dashboard/glucose-chart/chart-context.svelte.js";
  import { computeTrackLayout } from "#lib/components/dashboard/glucose-chart/engine/track-layout.js";
  import GlucoseTrack from "#lib/components/dashboard/glucose-chart/tracks/GlucoseTrack.svelte";
  import type { ChartThresholdsDto } from "#lib/api/generated/nocturne-api-client.js";
  import { resolveChartThresholds } from "#lib/constants/glucose-thresholds.js";
  import { tenantChartThresholds } from "#lib/stores/realtime-store.svelte.js";

  interface Props {
    /** Glucose entries for the day: { mills, mgdl } */
    entries: Array<{ mills: number; mgdl: number }>;
    /** The calendar day in Unix milliseconds: its start and its exclusive end, the next local midnight */
    day?: { startMills: number; endMills: number };
    /** Overrides the realtime store's tenant thresholds; the shared fallbacks apply when neither is present. */
    thresholds?: ChartThresholdsDto | null;
  }

  let { entries, day, thresholds: supplied }: Props = $props();

  const tenantThresholds = tenantChartThresholds();
  const thresholds = $derived(
    supplied ? resolveChartThresholds(supplied) : tenantThresholds.current
  );

  const glucoseData = $derived(
    entries
      .filter((e) => e.mgdl > 0)
      .map((e) => ({ time: new Date(e.mills), sgv: e.mgdl, color: "" }))
      .sort((a, b) => a.time.getTime() - b.time.getTime())
  );

  // Snap the x-domain to the calendar day, not the first/last reading.
  // Sparse days should show gaps instead of stretching partial data.
  const xDomain = $derived.by((): [Date, Date] | undefined => {
    if (day) return [new Date(day.startMills), new Date(day.endMills - 1)];

    if (entries.length === 0) return undefined;
    const ref = new Date(entries[0].mills);
    const y = ref.getFullYear();
    const m = ref.getMonth();
    const d = ref.getDate();
    return [
      new Date(y, m, d, 0, 0, 0, 0),
      new Date(y, m, d, 23, 59, 59, 999),
    ];
  });

  let chartHeight = $state(0);

  // GlucoseTrack reads only `glucoseData` and `thresholds`.
  const engineStub = partialEngine({
    get glucoseData() {
      return glucoseData;
    },
    get thresholds() {
      return thresholds;
    },
  });

  const layout = $derived(
    computeTrackLayout(
      chartHeight,
      thresholds.glucoseYMax,
      0,
      0,
      { basal: false, iob: false, cob: false },
      { pumpMode: false, override: false, profile: false, activity: false }
    )
  );

  setGlucoseChartContext({
    get engine() {
      return engineStub;
    },
    get layout() {
      return layout;
    },
  });
</script>

{#if entries.length > 0 && xDomain}
  <div class="h-full w-full">
    <Chart
      data={glucoseData}
      x={(d) => d.time}
      y={(d) => d.sgv}
      xScale={scaleTime()}
      {xDomain}
      yDomain={[0, thresholds.glucoseYMax]}
      padding={{ top: 1, bottom: 1, left: 1, right: 1 }}
    >
      {#snippet children({ context })}
        {((chartHeight = context.height), "")}
        <Svg>
          {#if chartHeight > 0}
            <GlucoseTrack
              lineColorMode="threshold"
              areaMode="deviation"
              showAxis={false}
              showPoints={false}
            />
          {/if}
        </Svg>
      {/snippet}
    </Chart>
  </div>
{/if}
