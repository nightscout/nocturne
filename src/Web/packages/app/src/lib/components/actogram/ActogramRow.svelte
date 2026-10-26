<script lang="ts" generics="T extends ActogramPoint">
  import type { Snippet } from 'svelte';
  import { Chart, Svg, Spline, Circle, Text, Tooltip } from 'layerchart';
  import { scaleTime } from 'd3-scale';
  import { timeScale } from '#lib/components/charts/scale-guards.js';
  import { curveMonotoneX } from 'd3';
  import {
    FALLBACK_GLUCOSE_Y_MAX,
    type ChartThresholds,
  } from '#lib/constants/glucose-thresholds.js';
  import { bg, bgLabel, time } from '#lib/utils/formatting.js';
  import { dashClass } from '#lib/components/charts/print/chart-print-patterns.js';
  import {
    MS_PER_HOUR,
    HOURS_PER_ROW,
    findNearestPoint,
    hasShiftedHourMarks,
    hourMarksOf,
    type ActogramDay,
    type ActogramPoint,
    type ActogramRowContext,
    type ActogramTooltipData,
    type GlucosePoint,
    type RowDataPoint,
    resolveTargetRange,
  } from './actogram';

  interface Props {
    day: ActogramDay;
    data: RowDataPoint<T>[];
    bgData: RowDataPoint<GlucosePoint>[];
    thresholds: ChartThresholds | undefined;
    height: number;
    row: Snippet<[ActogramRowContext<T>]>;
    tooltipValue?: Snippet<[{ point: T; day: ActogramDay }]>;
  }

  let { day, data, bgData, thresholds, height, row, tooltipValue }: Props = $props();

  // X domain: 0–48 hours from day start; the dimmed half starts where the served day ends.
  const dayStart = $derived(new Date(day.startMills));
  const xDomainEnd = $derived(new Date(day.startMills + HOURS_PER_ROW * MS_PER_HOUR));
  const midpoint = $derived(new Date(day.endMills));
  const shiftedHourMarks = $derived(hasShiftedHourMarks(day));

  // Map RowDataPoints to chart-plottable objects for BG overlay
  const bgChartData = $derived(
    bgData
      .toSorted((a, b) => a.hoursFromStart - b.hoursFromStart)
      .map((d) => ({
        time: new Date(day.startMills + d.hoursFromStart * MS_PER_HOUR),
        sgv: d.point.sgv,
        color: d.point.color,
      })),
  );
</script>

<div class="h-(--row-h)" style:--row-h="{height}px">
<Chart
  data={bgChartData}
  x="time"
  y="sgv"
  xScale={scaleTime()}
  xDomain={[dayStart, xDomainEnd]}
  yDomain={[0, thresholds?.glucoseYMax ?? FALLBACK_GLUCOSE_Y_MAX]}
  padding={{ left: 0, top: 0, bottom: 0, right: 0 }}
  tooltipContext={{ mode: "manual" }}
>
  {#snippet children({ context })}
    {@const rowContext: ActogramRowContext<T> = {
      xScale: timeScale(context.xScale),
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
        {@const target = resolveTargetRange(thresholds)}
        <!-- The dots' range colour is lost in black and white; these limits carry it instead. -->
        {#each [target.low, target.high] as limit, index (index)}
          <line
            data-testid="actogram-target-limit"
            x1={0}
            x2={context.width}
            y1={context.yScale(limit)}
            y2={context.yScale(limit)}
            stroke="var(--muted-foreground)"
            stroke-width={0.5}
            stroke-opacity={0.7}
            class="hidden [.chart-patterns-on_&]:inline {dashClass('target-range-limit')}"
          />
        {/each}
        <Spline
          data={bgChartData}
          x={(d) => d.time}
          y={(d) => d.sgv}
          curve={curveMonotoneX}
          class="stroke-muted-foreground/50 fill-none print:stroke-muted-foreground"
          strokeWidth={1.5}
        />
        <!-- Paper drops the dots: their range colour prints as grey, and grey
             dots read as the row's own marks. The target limits carry range.
             One data-mode Circle for the whole row, not a Circle per reading:
             each layerchart mark registers with the chart and every
             registration re-runs the chart's mark deriveds, so N points cost
             O(N^2). Data mode renders all points from a single mark. -->
        <Circle
          data-testid="actogram-glucose-reading"
          data={bgChartData}
          key={(d) => d.time}
          cx={(d) => d.time}
          cy={(d) => d.sgv}
          r={2}
          fill={(d) => d.color}
          class="opacity-80 print:hidden"
        />
      {/if}

      <!-- Dimming overlay for the extended (24–48h) half (top layer) -->
      <rect
        data-testid="actogram-dim-boundary"
        x={context.xScale(midpoint)}
        y={0}
        width={context.xScale(xDomainEnd) - context.xScale(midpoint)}
        height={context.height}
        fill="var(--background)"
        opacity={0.6}
      />

      <!-- The shared header sits on an even spacing, so a row a DST change shifted names its own hours. -->
      {#if shiftedHourMarks}
        {#each hourMarksOf(day) as mark, index (index)}
          <Text
            data-testid="actogram-hour-mark"
            value="{mark.hour}h"
            x={context.xScale(new Date(mark.mills))}
            y={1}
            textAnchor={index === 0 ? 'start' : 'middle'}
            verticalAnchor="start"
            class="fill-muted-foreground text-2xs"
          />
        {/each}
      {/if}

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
          const time = rowContext.xScale.invert(localX);
          const hoursFromStart = (time.getTime() - day.startMills) / MS_PER_HOUR;
          const nearestBg = findNearestPoint(bgData, hoursFromStart);
          const nearestData = findNearestPoint(data, hoursFromStart);
          context.tooltip?.show(e, { time, bgPoint: nearestBg, dataPoint: nearestData } satisfies ActogramTooltipData<T>);
        }}
        onpointerleave={() => context.tooltip?.hide()}
      />
    </Svg>

    <Tooltip.Root
      class="print:hidden rounded-lg border border-border px-2.5 py-1.5 shadow-xl"
    >
      {#snippet children({ data: tooltipData })}
        {@const d: ActogramTooltipData<T> | undefined = tooltipData}
        {#if d}
          <div class="space-y-1 text-xs">
            <div class="font-medium tabular-nums">
              {time(d.time)}
            </div>
            {#if d.bgPoint}
              <div class="flex items-center gap-1.5">
                <div class="size-2 rounded-full bg-(--dot)" style:--dot={d.bgPoint.point.color}></div>
                <span class="text-muted-foreground">Glucose</span>
                <span class="ml-auto font-mono font-medium tabular-nums">{bg(d.bgPoint.point.sgv)} {bgLabel()}</span>
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
