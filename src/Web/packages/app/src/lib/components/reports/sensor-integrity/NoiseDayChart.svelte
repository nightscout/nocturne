<script lang="ts">
  /**
   * One local day of glucose plotted against minutes-from-midnight, with detected noise clusters
   * drawn as full-height confidence-coloured annotation bands and hypo nadirs marked. Used both as
   * a compact strip row and, in `detailed` mode, as the expanded zoom view (points + hour axis).
   */
  import { Chart, Svg, Spline } from "layerchart";
  import { scaleLinear } from "d3-scale";
  import { curveMonotoneX } from "d3-shape";
  import { ClusterConfidence } from "$lib/api";
  import { categoryPatternClass } from "$lib/components/charts/print/chart-print-patterns";
  import type { DayBucket } from "./buckets";

  interface Props {
    bucket: DayBucket;
    /** Glucose reference thresholds (mg/dL). */
    thresholds?: { low: number; high: number };
    /** Shared y-axis max so rows are comparable. */
    yMax?: number;
    /** Expanded view: taller, with reading points and an hour axis. */
    detailed?: boolean;
  }

  let {
    bucket,
    thresholds = { low: 70, high: 180 },
    yMax = 300,
    detailed = false,
  }: Props = $props();

  const yMin = 40;

  // Hour ticks for the detailed axis: 00, 06, 12, 18, 24.
  const hourTicks = [0, 360, 720, 1080, 1440];

  // Confidence bands differ only by colour; in monochrome print they collapse to
  // near-identical greys, so each confidence gets a stable categorical texture.
  const bandStyle = (c: ClusterConfidence | undefined) => {
    if (c === ClusterConfidence.High)
      return { fill: "var(--cluster-high)", opacity: 0.26, patternSlot: 1 };
    if (c === ClusterConfidence.Medium)
      return { fill: "var(--cluster-medium)", opacity: 0.2, patternSlot: 2 };
    return { fill: "var(--cluster-low)", opacity: 0.16, patternSlot: 3 };
  };
</script>

<Chart
  data={bucket.points}
  x={(d) => d.x}
  y={(d) => d.y}
  xScale={scaleLinear()}
  xDomain={[0, 1440]}
  yScale={scaleLinear()}
  yDomain={[yMin, yMax]}
  padding={{ top: 2, bottom: detailed ? 18 : 2, left: 2, right: 2 }}
>
  {#snippet children({ context })}
    <Svg>
      <!-- Noise clusters as confidence-coloured bands (behind the trace) -->
      {#each bucket.bands as band, i (i)}
        {@const style = bandStyle(band.cluster.confidence)}
        {@const xPx = context.xScale(band.xStart)}
        <rect
          x={xPx}
          y={0}
          width={Math.max(1, context.xScale(band.xEnd) - xPx)}
          height={context.height}
          fill={style.fill}
          fill-opacity={style.opacity}
          class={categoryPatternClass(style.patternSlot)}
        />
      {/each}

      <!-- Low/high reference lines -->
      {#each [thresholds.low, thresholds.high] as level (level)}
        <line
          x1={0}
          x2={context.width}
          y1={context.yScale(level)}
          y2={context.yScale(level)}
          class="stroke-muted-foreground/25 [stroke-dasharray:3_2]"
        />
      {/each}

      <!-- Glucose trace -->
      <Spline
        curve={curveMonotoneX}
        class="fill-none stroke-muted-foreground [stroke-width:1.4]"
      />

      {#if detailed}
        {#each bucket.points as p, i (i)}
          <circle cx={context.xScale(p.x)} cy={context.yScale(p.y)} r={1.4} class="fill-foreground/60" />
        {/each}
        {#each hourTicks as tick (tick)}
          <text
            x={context.xScale(tick)}
            y={context.height + 12}
            text-anchor={tick === 0 ? "start" : tick === 1440 ? "end" : "middle"}
            class="fill-muted-foreground text-[9px]"
          >
            {String(tick / 60).padStart(2, "0")}:00
          </text>
        {/each}
      {/if}

      <!-- Hypo nadirs: marker above the lowest post-cluster reading -->
      {#each bucket.hypos as h, i (i)}
        {@const cx = context.xScale(h.x)}
        {@const cy = context.yScale(h.y)}
        <path
          d={`M ${cx - 3.5} ${cy - 8} L ${cx + 3.5} ${cy - 8} L ${cx} ${cy - 2} Z`}
          class={h.nocturnal ? "fill-cluster-high" : "fill-glucose-very-low"}
        />
      {/each}
    </Svg>
  {/snippet}
</Chart>
