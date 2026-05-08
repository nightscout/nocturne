<script lang="ts">
  /**
   * Tiny glucose curve for embedding in calendar day cells. Uses the shared
   * GlucoseTrack component with threshold-coloured line and deviation-fill
   * area, so the curve picks up the same visual contract as the dashboard
   * chart (red below low, in-range with no fill, red above high).
   *
   * Pure visual — no button, no click handling — so callers can wrap it
   * in whatever interactive element they need without nested-button HTML.
   */
  import { Chart, Svg } from "layerchart";
  import { scaleTime } from "d3-scale";
  import { setGlucoseChartContext } from "$lib/components/dashboard/glucose-chart/chart-context.svelte";
  import { computeTrackLayout } from "$lib/components/dashboard/glucose-chart/engine/track-layout";
  import GlucoseTrack from "$lib/components/dashboard/glucose-chart/tracks/GlucoseTrack.svelte";
  import type { ChartDataEngine } from "$lib/components/dashboard/glucose-chart/engine/chart-data-engine.svelte";

  // Calendar receives raw entries without threshold context. Defaults: legacy
  // sparkline values for low/high (70/180), ADA-style boundaries for veryLow/veryHigh.
  // If we ever pass per-tenant thresholds down to the calendar, replace this.
  const THRESHOLDS = {
    low: 70,
    high: 180,
    veryLow: 55,
    veryHigh: 250,
    glucoseYMax: 350,
  };

  interface Props {
    /** Glucose entries for the day: { mills, mgdl } */
    entries: Array<{ mills: number; mgdl: number }>;
  }

  let { entries }: Props = $props();

  const glucoseData = $derived(
    entries
      .filter((e) => e.mgdl > 0)
      .map((e) => ({ time: new Date(e.mills), sgv: e.mgdl, color: "" }))
      .sort((a, b) => a.time.getTime() - b.time.getTime()),
  );

  // Snap the x-domain to the local-day boundaries of the first entry so
  // the curve fills the cell horizontally even when the day starts/ends
  // with no readings.
  const xDomain = $derived.by(() => {
    if (entries.length === 0) return undefined;
    const ref = new Date(entries[0].mills);
    const y = ref.getFullYear();
    const m = ref.getMonth();
    const d = ref.getDate();
    return [
      new Date(y, m, d, 0, 0, 0, 0),
      new Date(y, m, d, 23, 59, 59, 999),
    ] as [Date, Date];
  });

  let chartHeight = $state(0);

  // Intentional escape hatch — engineStub doesn't satisfy the full ChartDataEngine
  // surface. GlucoseTrack only reads `glucoseData` and `thresholds`. If GlucoseTrack
  // starts touching more fields, this cast won't catch it; either widen the stub
  // or refactor GlucoseTrack to declare a narrower context type.
  const engineStub = {
    get glucoseData() {
      return glucoseData;
    },
    thresholds: THRESHOLDS,
  } as Partial<ChartDataEngine> as ChartDataEngine;

  const layout = $derived(
    computeTrackLayout(
      chartHeight,
      THRESHOLDS.glucoseYMax,
      0,
      0,
      { basal: false, iob: false, cob: false },
      { pumpMode: false, override: false, profile: false, activity: false },
    ),
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
  <Chart
    data={glucoseData}
    x={(d) => d.time}
    y={(d) => d.sgv}
    xScale={scaleTime()}
    {xDomain}
    yDomain={[0, THRESHOLDS.glucoseYMax]}
    padding={{ top: 1, bottom: 1, left: 1, right: 1 }}
  >
    {#snippet children({ context })}
      {(chartHeight = context.height, "")}
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
{/if}
