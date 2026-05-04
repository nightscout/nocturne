<script lang="ts">
  import type { Snippet } from 'svelte';
  import { Chart, Svg, Spline, Points, Tooltip } from 'layerchart';
  import { scaleTime } from 'd3-scale';
  import type { ScaleTime } from 'd3-scale';
  import { curveMonotoneX } from 'd3';
  import {
    MS_PER_HOUR,
    HOURS_PER_DAY,
    HOURS_PER_ROW,
    findNearestPoint,
    type ActogramPoint,
    type ActogramRowContext,
    type ActogramTooltipData,
    type GlucosePoint,
    type GlucoseThresholds,
    type RowDataPoint,
  } from './actogram';

  interface Props {
    day: Date;
    data: RowDataPoint<ActogramPoint>[];
    bgData: RowDataPoint<GlucosePoint>[];
    thresholds: GlucoseThresholds | undefined;
    height: number;
    row: Snippet<[ActogramRowContext]>;
    tooltipValue?: Snippet<[{ point: ActogramPoint; day: Date }]>;
  }

  let { day, data, bgData, thresholds, height, row, tooltipValue }: Props = $props();

  // X domain: 0–48 hours from day start
  const xDomainEnd = $derived(new Date(day.getTime() + HOURS_PER_ROW * MS_PER_HOUR));
  const midpoint = $derived(new Date(day.getTime() + HOURS_PER_DAY * MS_PER_HOUR));

  // Map RowDataPoints to chart-plottable objects for BG overlay
  const bgChartData = $derived(
    bgData
      .toSorted((a, b) => a.hoursFromStart - b.hoursFromStart)
      .map((d) => ({
        time: new Date(day.getTime() + d.hoursFromStart * MS_PER_HOUR),
        sgv: d.point.sgv,
        color: d.point.color,
      })),
  );
</script>

<div style:height="{height}px">
<Chart
  data={bgChartData}
  x="time"
  y="sgv"
  xScale={scaleTime()}
  xDomain={[day, xDomainEnd]}
  yDomain={[0, thresholds?.glucoseYMax ?? 300]}
  padding={{ left: 0, top: 0, bottom: 0, right: 0 }}
  tooltip={{ mode: "manual" }}
>
  {#snippet children({ context })}
    {@const rowContext: ActogramRowContext = {
      // LayerChart types xScale as AnyScale; we know it's ScaleTime because we pass scaleTime() above
      xScale: context.xScale as unknown as ScaleTime<number, number>,
      width: context.width,
      height: context.height,
      data,
      day,
    }}
    <Svg>
      <!-- Consumer's snippet renders first (bottom layer) -->
      {@render row(rowContext)}

      <!-- BG overlay line (middle layer) -->
      {#if bgChartData.length > 1 && thresholds}
        <Spline
          data={bgChartData}
          x={(d) => d.time}
          y={(d) => d.sgv}
          curve={curveMonotoneX}
          class="stroke-muted-foreground/50 fill-none"
          strokeWidth={1.5}
        />
        {#each bgChartData as point (point.time)}
          <Points
            data={[point]}
            x={(d) => d.time}
            y={(d) => d.sgv}
            r={2}
            fill={point.color}
            class="opacity-80"
          />
        {/each}
      {/if}

      <!-- Dimming overlay for the extended (24–48h) half (top layer) -->
      <rect
        x={context.xScale(midpoint)}
        y={0}
        width={context.xScale(xDomainEnd) - context.xScale(midpoint)}
        height={context.height}
        fill="var(--background)"
        opacity={0.6}
      />

      <!-- Interaction overlay for tooltip (topmost layer) -->
      <rect
        role="presentation"
        x={0}
        y={0}
        width={context.width}
        height={context.height}
        fill="transparent"
        onpointermove={(e) => {
          const svgRect = e.currentTarget.closest('svg')?.getBoundingClientRect();
          if (!svgRect) return;
          const localX = e.clientX - svgRect.left;
          const time = context.xScale.invert(localX);
          const hoursFromStart = (time.getTime() - day.getTime()) / MS_PER_HOUR;
          const nearestBg = findNearestPoint(bgData, hoursFromStart);
          const nearestData = findNearestPoint(data, hoursFromStart);
          context.tooltip?.show(e, { time, bgPoint: nearestBg, dataPoint: nearestData } satisfies ActogramTooltipData);
        }}
        onpointerleave={() => context.tooltip?.hide()}
      />
    </Svg>

    <Tooltip.Root
      class="bg-popover/95 text-popover-foreground rounded-lg border border-border px-2.5 py-1.5 shadow-xl"
    >
      {#snippet children({ data: tooltipData })}
        {@const d = tooltipData as ActogramTooltipData}
        {#if d}
          <div class="space-y-1 text-xs">
            <div class="font-medium tabular-nums">
              {d.time.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}
            </div>
            {#if d.bgPoint}
              <div class="flex items-center gap-1.5">
                <div class="size-2 rounded-full" style:background={d.bgPoint.point.color}></div>
                <span class="text-muted-foreground">Glucose</span>
                <span class="ml-auto font-mono font-medium tabular-nums">{Math.round(d.bgPoint.point.sgv)}</span>
              </div>
            {/if}
            {#if d.dataPoint && tooltipValue}
              <div class="flex items-center gap-1.5">
                {@render tooltipValue({ point: d.dataPoint.point, day })}
              </div>
            {/if}
          </div>
        {/if}
      {/snippet}
    </Tooltip.Root>
  {/snippet}
</Chart>
</div>
