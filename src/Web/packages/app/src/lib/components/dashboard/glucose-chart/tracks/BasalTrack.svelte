<script lang="ts">
  import {
    Area,
    Spline,
    Axis,
    Text,
    Group,
    ChartClipPath,
    Highlight,
    AnnotationRange,
    AnnotationLine,
    AnnotationPoint,
    getChartContext,
  } from "layerchart";
  import { curveStepAfter } from "d3";
  import { BasalDeliveryOrigin } from "$lib/api";
  import { getGlucoseChartContext } from "../chart-context.svelte";

  interface Props {
    onPointClick?: (time: Date) => void;
  }

  let { onPointClick }: Props = $props();

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  const basalData = $derived(ctx.engine.basalData);
  const scheduledBasalData = $derived(ctx.engine.scheduledBasalData);
  const tempBasalSpans = $derived(ctx.engine.displayTempBasalSpans);
  const staleBasalData = $derived(ctx.engine.staleBasalData);
  const maxBasalRate = $derived(ctx.engine.maxBasalRate);
  const basalLayout = $derived(ctx.layout.basal);

  const effectiveOnPointClick = $derived(
    onPointClick ?? ((time: Date) => ctx.inspection?.inspectFromTrack(time))
  );

  // Group consecutive basal points by origin for proper layered rendering
  // This ensures each origin type (Scheduled, Algorithm, Manual, Suspended) is rendered as a distinct segment
  const basalSegmentsByOrigin = $derived.by(() => {
    type BasalDataPoint = (typeof basalData)[number];
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

{#if basalLayout}
  {@const basalScale = basalLayout.scale}
  {@const basalZero = basalLayout.zero}
  {@const basalTrackTop = basalLayout.top}
  {@const basalAxisScale = basalLayout.axisScale}

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
          x={chartCtx.xScale(span.displayStart)}
          y={chartCtx.yScale(basalScale(maxBasalRate * 0.8))}
        >
          <Text x={4} y={0} class="text-[7px] fill-insulin-basal font-medium">
            {span.rate.toFixed(2)}U/h
          </Text>
        </Group>
      {:else if span.percent !== null}
        <Group
          x={chartCtx.xScale(span.displayStart)}
          y={chartCtx.yScale(basalScale(maxBasalRate * 0.8))}
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
    <ChartClipPath>
      {#each basalSegmentsByOrigin as segment, i (i)}
        {@const pattern = getBasalPattern(segment.origin)}
        {@const opacity = getBasalOpacity(segment.origin)}
        {@const fillColor = segment.points[0].fillColor}
        {@const strokeColor = segment.points[0].strokeColor}
        {#if pattern}
          <!-- Use AnnotationRange for segments with patterns (Inferred) -->
          {#each segment.points as point, pointIdx (point.timestamp)}
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
    </ChartClipPath>
  {/if}

  <!-- Basal highlight for point click -->
  <ChartClipPath>
    <Highlight
      x={(d) => d.time}
      y={(d) => {
        const timeMs = d.time.getTime();
        let nearest: (typeof basalData)[number] | undefined;
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
      onPointClick={effectiveOnPointClick
        ? (_e, { data }) => effectiveOnPointClick(data.time)
        : undefined}
    />
  </ChartClipPath>
{/if}
