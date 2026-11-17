<script lang="ts">
  /**
   * One numeric lane of the therapy timeline: a label, a plot on the shared time
   * axis, and the lane's value range. Every lane uses the same column widths, so
   * plots line up without a shared chart.
   */
  import { Chart, Svg, Area, Spline } from "layerchart";
  import { scaleLinear } from "d3-scale";
  import { curveLinear, curveStepAfter } from "d3";

  export interface LanePoint {
    t: number;
    v: number;
  }

  export interface LaneBandPoint {
    t: number;
    low: number;
    high: number;
  }

  export interface LaneArea {
    key: string;
    points: LanePoint[];
    class: string;
  }

  export interface LaneBand {
    key: string;
    points: LaneBandPoint[];
    class: string;
    /** Whether the band holds its value until the next point. */
    step?: boolean;
  }

  export interface LaneLine {
    key: string;
    points: LanePoint[];
    class: string;
    /** Whether the line holds its value until the next point. */
    step?: boolean;
    dash?: string;
  }

  interface Props {
    label: string;
    unit?: string;
    xDomain: [number, number];
    yDomain: [number, number];
    areas?: LaneArea[];
    bands?: LaneBand[];
    lines?: LaneLine[];
    axisFormat?: (value: number) => string;
    heightClass?: string;
  }

  let {
    label,
    unit = "",
    xDomain,
    yDomain,
    areas = [],
    bands = [],
    lines = [],
    axisFormat = (v) => String(v),
    heightClass = "h-16",
  }: Props = $props();
</script>

<div class="grid grid-cols-[var(--tl-label)_minmax(0,1fr)_var(--tl-axis)] border-t border-border">
  <div class="flex flex-col justify-center pr-2 py-1">
    <span class="text-xs font-medium leading-tight">{label}</span>
    {#if unit}
      <span class="text-2xs text-muted-foreground leading-tight">{unit}</span>
    {/if}
  </div>
  <div class={heightClass}>
    <Chart
      data={[]}
      xScale={scaleLinear()}
      {xDomain}
      yScale={scaleLinear()}
      {yDomain}
      padding={{ top: 6, bottom: 6, left: 0, right: 0 }}
    >
      <Svg>
        {#each bands as band (band.key)}
          {#if band.points.length > 1}
            <Area
              data={band.points}
              x={(d: LaneBandPoint) => d.t}
              y0={(d: LaneBandPoint) => d.low}
              y1={(d: LaneBandPoint) => d.high}
              curve={band.step ? curveStepAfter : curveLinear}
              class={band.class}
            />
          {/if}
        {/each}
        {#each areas as area (area.key)}
          {#if area.points.length > 1}
            <Area
              data={area.points}
              x={(d: LanePoint) => d.t}
              y0={() => yDomain[0]}
              y1={(d: LanePoint) => d.v}
              curve={curveStepAfter}
              class={area.class}
            />
          {/if}
        {/each}
        {#each lines as line (line.key)}
          {#if line.points.length > 1}
            <Spline
              data={line.points}
              x={(d: LanePoint) => d.t}
              y={(d: LanePoint) => d.v}
              curve={line.step ? curveStepAfter : curveLinear}
              class="fill-none {line.class}"
              stroke-dasharray={line.dash}
            />
          {/if}
        {/each}
      </Svg>
    </Chart>
  </div>
  <div
    class="flex flex-col justify-between py-1 pl-2 text-2xs tabular-nums text-muted-foreground"
    aria-hidden="true"
  >
    <span>{axisFormat(yDomain[1])}</span>
    <span>{axisFormat(yDomain[0])}</span>
  </div>
</div>
