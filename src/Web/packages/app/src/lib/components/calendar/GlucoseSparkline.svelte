<script lang="ts">
  /**
   * Tiny glucose curve for embedding in calendar day cells. Renders a
   * smoothed line spanning a full local-day window with low/high regions
   * tinted via Threshold/Area, plus a soft in-range backdrop band.
   *
   * Pure visual — no button, no click handling — so callers can wrap it
   * in whatever interactive element they need without nested-button HTML.
   */
  import {
    Chart,
    Svg,
    Spline,
    Area,
    Threshold,
    AnnotationRange,
  } from "layerchart";
  import { curveMonotoneX } from "d3-shape";

  // Glucose thresholds (mg/dL) and y-axis bounds match DayGlucoseProfile.
  const LOW_THRESHOLD = 70;
  const HIGH_THRESHOLD = 180;
  const Y_MIN = 40;
  const Y_MAX = 350;

  interface Props {
    /** Glucose entries for the day: { mills, mgdl } */
    entries: Array<{ mills: number; mgdl: number }>;
  }

  let { entries }: Props = $props();

  const chartData = $derived(
    entries.map((e) => ({ date: new Date(e.mills), value: e.mgdl })),
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

  const yDomain = [Y_MIN, Y_MAX] as [number, number];
</script>

{#if entries.length > 0 && xDomain}
  <Chart
    data={chartData}
    x="date"
    y="value"
    {xDomain}
    {yDomain}
    padding={{ top: 1, bottom: 1, left: 1, right: 1 }}
  >
    <Svg>
      <AnnotationRange
        y={[LOW_THRESHOLD, HIGH_THRESHOLD]}
        class="fill-glucose-in-range opacity-20"
      />
      <Threshold curve={curveMonotoneX}>
        {#snippet above()}
          <Area
            y0={HIGH_THRESHOLD}
            curve={curveMonotoneX}
            class="fill-red-500"
            line={{ class: "stroke-none" }}
          />
        {/snippet}
        {#snippet below()}
          <Area
            y0={LOW_THRESHOLD}
            curve={curveMonotoneX}
            class="fill-glucose-low opacity-50"
            line={{ class: "stroke-none" }}
          />
        {/snippet}
        <Spline
          curve={curveMonotoneX}
          class="stroke-glucose-in-range stroke-[1.5] fill-none"
        />
      </Threshold>
    </Svg>
  </Chart>
{/if}
