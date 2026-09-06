<script lang="ts">
  // Per-span geometry is native SVG: layerchart marks register with the chart on
  // mount and its mark deriveds re-run over every mark, so one AnnotationRange
  // per temp basal / step cost O(N^2). Pattern is not a mark and stays.
  import {
    Area,
    Spline,
    Axis,
    Pattern,
    ChartClipPath,
    Highlight,
    AnnotationLine,
    AnnotationPoint,
  } from "layerchart";
  import { curveStepAfter } from "d3";
  import type { ScaleLinear } from "d3-scale";
  import { BasalDeliveryOrigin } from "../enums.js";

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
    onPointClick?: (time: Date) => void;
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
    onPointClick,
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

    // Add closing points so curveStepAfter renders the last step of each segment.
    // Without this, a segment with a single point (e.g. a temp basal immediately
    // followed by a suspended period) produces no visible area.
    for (let i = 0; i < segments.length; i++) {
      const seg = segments[i];
      const lastPoint = seg.points[seg.points.length - 1];
      const nextSegFirstPoint = segments[i + 1]?.points[0];

      if (nextSegFirstPoint && lastPoint.timestamp !== nextSegFirstPoint.timestamp) {
        seg.points.push({
          ...lastPoint,
          timestamp: nextSegFirstPoint.timestamp,
        });
      }
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
      {@const spanLeft = context.xScale(span.displayStart)}
      {@const spanUpper = context.yScale(basalScale(maxBasalRate * 0.9))}
      {@const spanLower = context.yScale(basalScale(maxBasalRate * 0.7))}
      {@const labelX = spanLeft + 4}
      {@const labelY = context.yScale(basalScale(maxBasalRate * 0.8))}
      <rect
        x={spanLeft}
        y={Math.min(spanUpper, spanLower)}
        width={context.xScale(span.displayEnd) - spanLeft}
        height={Math.abs(spanLower - spanUpper)}
        fill={span.color}
        class="opacity-40"
      />
      <!-- Show temp basal rate label -->
      {#if span.rate !== null}
        <text
          x={labelX}
          y={labelY}
          dy="-0.355em"
          class="text-[7px] fill-insulin-basal font-medium"
        >
          {span.rate.toFixed(2)}U/h
        </text>
      {:else if span.percent !== null}
        <text
          x={labelX}
          y={labelY}
          dy="-0.355em"
          class="text-[7px] fill-insulin-basal font-medium"
        >
          {span.percent}%
        </text>
      {/if}
    {/each}
  </ChartClipPath>

  <!-- Stale basal data indicator -->
  {#if staleBasalData}
    <ChartClipPath>
      {@const staleLeft = context.xScale(staleBasalData.start)}
      {@const staleWidth = context.xScale(staleBasalData.end) - staleLeft}
      {@const staleTop = context.yScale(basalScale(maxBasalRate))}
      {@const staleBottom = context.yScale(basalZero)}
      <Pattern size={8} lines={{ rotate: -45, opacity: 0.1 }}>
        {#snippet children({ pattern }: { pattern: string })}
          <rect
            x={staleLeft}
            y={Math.min(staleTop, staleBottom)}
            width={staleWidth}
            height={Math.abs(staleBottom - staleTop)}
            fill={pattern}
          />
        {/snippet}
      </Pattern>
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
  <text
    x={4}
    y={basalTrackTop + 12}
    dy="-0.355em"
    class="text-[8px] fill-muted-foreground font-medium"
  >
    BASAL
  </text>

  <!-- Basal area - render each segment by origin with actual delivered rate -->
  {#if basalData.length > 0}
    {#each basalSegmentsByOrigin as segment, i (i)}
      {@const pattern = getBasalPattern(segment.origin)}
      {@const opacity = getBasalOpacity(segment.origin)}
      {@const fillColor = segment.points[0].fillColor}
      {@const strokeColor = segment.points[0].strokeColor}
      {#if pattern}
        <!-- Hatched step rects for segments with patterns (Inferred) -->
        <Pattern size={pattern.size} lines={pattern.lines}>
          {#snippet children({ pattern: patternFill }: { pattern: string })}
            {#each segment.points.slice(0, -1) as point, pointIdx (pointIdx)}
              {@const nextPoint = segment.points[pointIdx + 1]}
              {@const left = context.xScale(new Date(point.timestamp ?? 0))}
              {@const top = context.yScale(basalScale(point.rate ?? 0))}
              {@const bottom = context.yScale(basalZero)}
              {@const step = {
                x: left,
                y: Math.min(top, bottom),
                width: context.xScale(new Date(nextPoint.timestamp ?? 0)) - left,
                height: Math.abs(bottom - top),
              }}
              <rect {...step} fill={fillColor} style="opacity: {opacity}" />
              <rect {...step} fill={patternFill} style="opacity: {opacity}" />
            {/each}
          {/snippet}
        </Pattern>
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

  <!-- Basal highlight for point click -->
  <ChartClipPath>
    <Highlight
      x={(d) => d.time}
      y={(d) => {
        const timeMs = d.time.getTime();
        let nearest: BasalDataPoint | undefined;
        for (let i = basalData.length - 1; i >= 0; i--) {
          if ((basalData[i].timestamp ?? 0) <= timeMs) {
            nearest = basalData[i];
            break;
          }
        }
        if (!nearest || nearest.rate == null) return null;
        return basalScale(nearest.rate);
      }}
      points={{ class: "fill-iob-basal" }}
      onPointClick={onPointClick
        ? (_e, { data }) => onPointClick(data.time)
        : undefined}
    />
  </ChartClipPath>
{/if}
