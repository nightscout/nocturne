<script lang="ts">
  import {
    Area,
    Spline,
    Axis,
    Text,
    Group,
    ChartClipPath,
    AnnotationRange,
    AnnotationLine,
    AnnotationPoint,
  } from "layerchart";
  import { curveStepAfter } from "d3";
  import type { ScaleLinear } from "d3-scale";
  import { BasalDeliveryOrigin } from "$lib/api";

  interface BasalDataPoint {
    timestamp?: number;
    rate?: number;
    scheduledRate?: number;
    origin?: BasalDeliveryOrigin;
    fillColor: string;
    strokeColor: string;
  }

  interface TempBasalSpan {
    id?: string;
    displayStart: Date;
    displayEnd: Date;
    color: string;
    rate: number | null;
    percent: number | null;
  }

  interface StaleBasalData {
    start: Date;
    end: Date;
  }

  interface Props {
    basalData: BasalDataPoint[];
    scheduledBasalData: { timestamp?: number; rate?: number }[];
    tempBasalSpans: TempBasalSpan[];
    staleBasalData: StaleBasalData | null;
    maxBasalRate: number;
    basalScale: (rate: number) => number;
    basalZero: number;
    basalTrackTop: number;
    basalAxisScale: ScaleLinear<number, number>;
    context: {
      xScale: (time: Date) => number;
      yScale: (value: number) => number;
    };
    showBasal: boolean;
  }

  let {
    basalData,
    scheduledBasalData,
    tempBasalSpans,
    staleBasalData,
    maxBasalRate,
    basalScale,
    basalZero,
    basalTrackTop,
    basalAxisScale,
    context,
    showBasal,
  }: Props = $props();

  // Group consecutive basal points by origin for proper layered rendering
  // This ensures each origin type (Scheduled, Algorithm, Manual, Suspended) is rendered as a distinct segment
  const basalSegmentsByOrigin = $derived.by(() => {
    type Segment = { origin: BasalDeliveryOrigin; points: BasalDataPoint[] };
    const segments: Segment[] = [];
    let currentSegment: Segment | null = null;

    for (const point of basalData) {
      const origin = point?.origin ?? BasalDeliveryOrigin.Scheduled;

      if (!currentSegment || currentSegment.origin !== origin) {
        // Start a new segment
        if (currentSegment && currentSegment.points.length > 0) {
          segments.push(currentSegment);
        }
        currentSegment = { origin, points: [point] };
      } else {
        // Continue current segment
        currentSegment.points.push(point);
      }
    }

    // Don't forget the last segment
    if (currentSegment && currentSegment.points.length > 0) {
      segments.push(currentSegment);
    }

    return segments;
  });

  // Get opacity based on basal delivery origin
  function getBasalOpacity(origin: BasalDeliveryOrigin): number {
    switch (origin) {
      case BasalDeliveryOrigin.Algorithm:
        return 0.8;
      case BasalDeliveryOrigin.Manual:
        return 0.9;
      case BasalDeliveryOrigin.Suspended:
        return 0.5;
      case BasalDeliveryOrigin.Inferred:
        return 0.4;
      case BasalDeliveryOrigin.Scheduled:
      default:
        return 0.6;
    }
  }

  // Get pattern for basal delivery origin (only Inferred uses hatching)
  function getBasalPattern(origin: BasalDeliveryOrigin): { size: number; lines: { rotate: number; opacity: number } } | undefined {
    if (origin === BasalDeliveryOrigin.Inferred) {
      return { size: 8, lines: { rotate: -45, opacity: 0.3 } };
    }
    return undefined;
  }
</script>

{#if showBasal}
  <ChartClipPath>
    <!-- Temp basal span indicators (shown in basal track when basal is visible) -->
    {#each tempBasalSpans as span (span.id)}
      <AnnotationRange
        x={[span.displayStart.getTime(), span.displayEnd.getTime()]}
        y={[basalScale(maxBasalRate * 0.9), basalScale(maxBasalRate * 0.7)]}
        fill={span.color}
        class="opacity-40"
      />
      <!-- Show temp basal rate label -->
      {#if span.rate !== null}
        <Group
          x={context.xScale(span.displayStart)}
          y={context.yScale(basalScale(maxBasalRate * 0.8))}
        >
          <Text x={4} y={0} class="text-[7px] fill-insulin-basal font-medium">
            {span.rate.toFixed(2)}U/h
          </Text>
        </Group>
      {:else if span.percent !== null}
        <Group
          x={context.xScale(span.displayStart)}
          y={context.yScale(basalScale(maxBasalRate * 0.8))}
        >
          <Text x={4} y={0} class="text-[7px] fill-insulin-basal font-medium">
            {span.percent}%
          </Text>
        </Group>
      {/if}
    {/each}
  </ChartClipPath>

  <!-- Stale basal data indicator -->
  {#if staleBasalData}
    <ChartClipPath>
      <AnnotationRange
        x={[staleBasalData.start.getTime(), staleBasalData.end.getTime()]}
        y={[basalScale(maxBasalRate), basalZero]}
        pattern={{
          size: 8,
          lines: {
            rotate: -45,
            opacity: 0.1,
          },
        }}
      />
    </ChartClipPath>
    <AnnotationLine
      x={staleBasalData.start}
      class="stroke-yellow-500/50 stroke-1"
      stroke-dasharray="2,2"
    />
    <AnnotationPoint
      x={staleBasalData.start.getTime()}
      y={basalScale(maxBasalRate)}
      label="Last pump sync"
      labelPlacement="bottom-right"
      fill="yellow"
      class="hover:bg-background hover:text-foreground"
    />
  {/if}

  <!-- Scheduled basal rate line -->
  {#if scheduledBasalData.length > 0}
    <Spline
      data={scheduledBasalData}
      x={(d) => new Date(d.timestamp ?? 0)}
      y={(d) => basalScale(d.rate ?? 0)}
      curve={curveStepAfter}
      class="stroke-muted-foreground/50 stroke-1 fill-none"
      stroke-dasharray="4,4"
    />
  {/if}

  <!-- Basal axis on right -->
  <Axis
    placement="right"
    scale={basalAxisScale}
    ticks={2}
    tickLabelProps={{
      class: "text-[9px] fill-muted-foreground",
    }}
  />

  <!-- Basal track label -->
  <Text
    x={4}
    y={basalTrackTop + 12}
    class="text-[8px] fill-muted-foreground font-medium"
  >
    BASAL
  </Text>

  <!-- Basal area - render each segment by origin with actual delivered rate -->
  {#if basalData.length > 0}
    {#each basalSegmentsByOrigin as segment, i (i)}
      {@const pattern = getBasalPattern(segment.origin)}
      {@const opacity = getBasalOpacity(segment.origin)}
      {@const fillColor = segment.points[0].fillColor}
      {@const strokeColor = segment.points[0].strokeColor}
      {#if pattern}
        <!-- Use AnnotationRange for segments with patterns (Inferred) -->
        {#each segment.points as point, pointIdx}
          {#if pointIdx < segment.points.length - 1}
            {@const nextPoint = segment.points[pointIdx + 1]}
            <AnnotationRange
              x={[point.timestamp ?? 0, nextPoint.timestamp ?? 0]}
              y={[basalScale(point.rate ?? 0), basalZero]}
              fill={fillColor}
              {pattern}
              style="opacity: {opacity}"
            />
          {/if}
        {/each}
      {:else}
        <!-- Use Area for segments without patterns -->
        <Area
          data={segment.points}
          x={(d) => new Date(d.timestamp ?? 0)}
          y0={() => basalZero}
          y1={(d) => basalScale(d.rate ?? 0)}
          curve={curveStepAfter}
          fill={fillColor}
          stroke={strokeColor}
          class="stroke-1"
          style="opacity: {opacity}"
        />
      {/if}
    {/each}
  {/if}
{/if}
