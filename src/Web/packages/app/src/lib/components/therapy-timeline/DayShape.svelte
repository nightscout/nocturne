<script lang="ts">
  /** The 24-hour shape of one schedule of a profile version. */
  import { Chart, Svg, Area, Spline } from "layerchart";
  import { scaleLinear } from "d3-scale";
  import { curveStepAfter } from "d3";
  import type { ProfileHistoryEntry } from "#lib/api/index.js";

  interface Props {
    entries: ProfileHistoryEntry[];
    /** The value range shared by every version in the list, so shapes compare. */
    yDomain: [number, number];
    /** Whether the entries carry a low and a high. */
    range?: boolean;
  }

  let { entries, yDomain, range = false }: Props = $props();

  interface Point {
    t: number;
    low: number;
    high: number;
  }

  const points = $derived.by((): Point[] => {
    if (entries.length === 0) return [];
    const day = entries.map((e) => ({
      t: e.timeAsSeconds ?? 0,
      low: e.value ?? 0,
      high: e.high ?? e.value ?? 0,
    }));
    return [...day, { ...day[day.length - 1], t: 86_400 }];
  });
</script>

{#if points.length > 0}
  <Chart
    data={[]}
    xScale={scaleLinear()}
    xDomain={[0, 86_400]}
    yScale={scaleLinear()}
    {yDomain}
    padding={{ top: 3, bottom: 3, left: 0, right: 0 }}
  >
    <Svg>
      {#if range}
        <Area
          data={points}
          x={(d: Point) => d.t}
          y0={(d: Point) => d.low}
          y1={(d: Point) => d.high}
          curve={curveStepAfter}
          class="fill-glucose-in-range/30 stroke-glucose-in-range stroke-1"
        />
      {:else}
        <Area
          data={points}
          x={(d: Point) => d.t}
          y0={() => yDomain[0]}
          y1={(d: Point) => d.low}
          curve={curveStepAfter}
          class="fill-foreground/8"
        />
      {/if}
      <Spline
        data={points}
        x={(d: Point) => d.t}
        y={(d: Point) => d.low}
        curve={curveStepAfter}
        class="fill-none stroke-foreground/70 stroke-2"
      />
    </Svg>
  </Chart>
{/if}
