<script lang="ts">
  /**
   * Mini glucose profile chart for calendar day cells.
   * Shows a smooth line of glucose readings with colored fills
   * for out-of-range areas (low/high).
   * Fills the entire calendar cell.
   * Uses LayerChart for rendering.
   */
  import { Chart, Svg, Spline, Area, Threshold, AnnotationRange } from "layerchart";
  import { curveMonotoneX } from "d3-shape";

  // Glucose range thresholds (mg/dL)
  const LOW_THRESHOLD = 70;
  const HIGH_THRESHOLD = 180;

  // Y-axis range
  const Y_MIN = 40;
  const Y_MAX = 350;

  interface Props {
    /** Glucose entries for the day: { mills, sgv } */
    entries: Array<{ mills: number; sgv: number }>;
    /** Click handler */
    onclick?: () => void;
  }

  let { entries, onclick }: Props = $props();

  // Transform entries for LayerChart
  const chartData = $derived(
    entries.map((e) => ({
      date: new Date(e.mills),
      value: e.sgv,
    }))
  );

  // X domain: full day boundaries based on first entry
  const xDomain = $derived.by(() => {
    if (entries.length === 0) return undefined;

    const firstMills = entries[0].mills;
    const dayStart = new Date(firstMills);
    dayStart.setHours(0, 0, 0, 0);
    const dayEnd = new Date(firstMills);
    dayEnd.setHours(23, 59, 59, 999);

    return [dayStart, dayEnd] as [Date, Date];
  });

  // Y domain: fixed range for glucose values
  const yDomain = [Y_MIN, Y_MAX] as [number, number];

  const hasData = $derived(entries.length > 0);
</script>

<button
  type="button"
  class="absolute inset-0 cursor-pointer focus:outline-none focus:ring-2 focus:ring-primary focus:ring-inset rounded-lg"
  {onclick}
>
  {#if hasData && xDomain}
    <Chart
      data={chartData}
      x="date"
      y="value"
      {xDomain}
      {yDomain}
      padding={{ top: 1, bottom: 1, left: 1, right: 1 }}
    >
      <Svg>
        <!-- Target range band (in-range background) -->
        <AnnotationRange
          y={[LOW_THRESHOLD, HIGH_THRESHOLD]}
          class="fill-glucose-in-range opacity-20"
        />
          <!-- Threshold fills for out-of-range areas -->
          <Threshold curve={curveMonotoneX}>
            {#snippet above()}
              <!-- High area (above the line, clipped to show only where data exceeds high threshold) -->
              <Area
                y0={HIGH_THRESHOLD}
                curve={curveMonotoneX}
                class="fill-red-500"
                line={{ class: "stroke-none" }}
              />
            {/snippet}
            {#snippet below()}
              <!-- Low area (below the line, clipped to show only where data is below low threshold) -->
              <Area
                y0={LOW_THRESHOLD}
                curve={curveMonotoneX}
                class="fill-glucose-low opacity-50"
                line={{ class: "stroke-none" }}
              />
            {/snippet}
            <!-- Glucose line -->
            <Spline curve={curveMonotoneX} class="stroke-glucose-in-range stroke-[1.5] fill-none" />
          </Threshold>
      </Svg>
    </Chart>
  {:else}
    <svg width="100%" height="100%" class="rounded-lg overflow-hidden">
      <text
        x="50%"
        y="50%"
        text-anchor="middle"
        dominant-baseline="middle"
        class="fill-muted-foreground text-[8px]"
      >
        No data
      </text>
    </svg>
  {/if}
</button>
