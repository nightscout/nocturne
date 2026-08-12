<script lang="ts">
  /**
   * Paired readings plotted against each other, reference device on x. Points sit on the
   * diagonal where the two devices read the same value.
   */
  import { Chart, Svg } from "layerchart";
  import { scaleLinear } from "d3-scale";
  import { bg, bgLabel } from "$lib/utils/formatting";
  import type { CgmPairedReading } from "$lib/api";

  interface Props {
    pairs: CgmPairedReading[];
    /** Axis label for the device plotted on y. */
    nameA: string;
    /** Axis label for the reference device, plotted on x. */
    nameB: string;
  }

  let { pairs, nameA, nameB }: Props = $props();

  const points = $derived(
    pairs.map((p) => ({ x: p.mgdlB ?? 0, y: p.mgdlA ?? 0 }))
  );

  const domain = $derived.by(() => {
    const values = points.flatMap((p) => [p.x, p.y]);
    return [Math.min(40, ...values), Math.max(400, ...values)];
  });

  const ticks = $derived.by(() => {
    const [min, max] = domain;
    const step = (max - min) / 4;
    return [0, 1, 2, 3, 4].map((i) => Math.round(min + step * i));
  });

  const radius = 1.6;

  /**
   * Every point in a single path: thousands of pairs would otherwise be thousands of marks,
   * which registration cost makes quadratic.
   */
  function dots(xScale: (value: number) => number, yScale: (value: number) => number): string {
    let d = "";
    for (const point of points) {
      const cx = xScale(point.x);
      const cy = yScale(point.y);
      d += `M${cx - radius},${cy}a${radius},${radius} 0 1,0 ${radius * 2},0a${radius},${radius} 0 1,0 ${-radius * 2},0`;
    }
    return d;
  }
</script>

<div class="h-80 w-full">
  <Chart
    data={points}
    x="x"
    y="y"
    xScale={scaleLinear()}
    xDomain={domain}
    yScale={scaleLinear()}
    yDomain={domain}
    padding={{ top: 8, bottom: 28, left: 48, right: 8 }}
  >
    {#snippet children({ context })}
      <Svg>
        <line
          x1={context.xScale(domain[0])}
          y1={context.yScale(domain[0])}
          x2={context.xScale(domain[1])}
          y2={context.yScale(domain[1])}
          class="stroke-muted-foreground/40 [stroke-dasharray:4_3]"
        />

        <path
          data-testid="paired-points"
          d={dots(context.xScale, context.yScale)}
          class="fill-primary/50"
        />

        {#each ticks as tick (tick)}
          <text
            x={context.xScale(tick)}
            y={context.height + 14}
            text-anchor="middle"
            class="fill-muted-foreground text-[10px]"
          >
            {bg(tick)}
          </text>
          <text
            x={-8}
            y={context.yScale(tick) + 3}
            text-anchor="end"
            class="fill-muted-foreground text-[10px]"
          >
            {bg(tick)}
          </text>
        {/each}
      </Svg>
    {/snippet}
  </Chart>
</div>

<div class="mt-1 flex justify-between text-xs text-muted-foreground">
  <span>Vertical: {nameA} ({bgLabel()})</span>
  <span>Horizontal: {nameB} ({bgLabel()})</span>
</div>
