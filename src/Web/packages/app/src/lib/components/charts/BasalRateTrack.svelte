<script lang="ts">
  import {
    Group,
    Area,
    Spline,
    Text,
    Axis,
    AnnotationRange,
    ChartClipPath,
    AnnotationLine,
    AnnotationPoint,
  } from "layerchart";
  import { curveStepAfter } from "d3";
  import { scaleLinear } from "d3-scale";
  import { PumpModeIcon } from "$lib/components/icons";
  import { BasalDeliveryOrigin } from "$lib/api";

  interface StateSpanChartData {
    id: string;
    state: string;
    startTime: Date;
    endTime: Date | null;
    color: string;
  }

  interface BasalDeliveryChartData {
    id: string;
    startTime: Date;
    endTime: Date | null;
    rate: number;
    origin: BasalDeliveryOrigin;
    source?: string;
    color: string;
  }

  interface BasalDataPoint {
    time: Date;
    rate: number;
    scheduledRate?: number;
    isTemp?: boolean;
  }

  interface BasalDeliverySpanWithDisplay extends BasalDeliveryChartData {
    displayStart: Date;
    displayEnd: Date;
  }

  interface Props {
    /** Basal rate data series (legacy - used when basalDeliverySpans not provided) */
    basalData?: BasalDataPoint[];
    /** Scheduled basal data series (profile baseline) */
    scheduledBasalData?: { time: Date; rate: number }[];
    /** Maximum basal rate for Y-axis scaling */
    maxBasalRate: number;
    /** Pump mode spans for background coloring */
    pumpModeSpans?: (StateSpanChartData & {
      displayStart: Date;
      displayEnd: Date;
    })[];
    /** Basal delivery spans from StateSpans API */
    basalDeliverySpans?: BasalDeliverySpanWithDisplay[];
    /** Stale basal data indicator */
    staleBasalData?: { start: Date; end: Date } | null;
    /** Track height in pixels */
    trackHeight: number;
    /** Track top position in pixels (from chart top) */
    trackTop: number;
    /** Total chart height for scale calculations */
    chartHeight: number;
    /** Glucose Y max for domain conversion */
    glucoseYMax: number;
    /** Chart context with xScale and yScale */
    context: {
      xScale: (date: Date) => number;
      yScale: (value: number) => number;
    };
    /** Optional: show axis */
    showAxis?: boolean;
    /** Optional: show label */
    showLabel?: boolean;
  }

  let {
    basalData = [],
    scheduledBasalData = [],
    maxBasalRate,
    pumpModeSpans = [],
    basalDeliverySpans = [],
    staleBasalData = null,
    trackHeight,
    trackTop,
    chartHeight,
    glucoseYMax,
    context,
    showAxis = true,
    showLabel = true,
  }: Props = $props();

  // Group basal delivery spans by origin for layered rendering
  const scheduledDeliverySpans = $derived(
    basalDeliverySpans.filter((s) => s.origin === BasalDeliveryOrigin.Scheduled)
  );
  const algorithmDeliverySpans = $derived(
    basalDeliverySpans.filter((s) => s.origin === BasalDeliveryOrigin.Algorithm)
  );
  const manualDeliverySpans = $derived(
    basalDeliverySpans.filter((s) => s.origin === BasalDeliveryOrigin.Manual)
  );
  const suspendedDeliverySpans = $derived(
    basalDeliverySpans.filter((s) => s.origin === BasalDeliveryOrigin.Suspended)
  );

  // Check if we're using new StateSpans-based basal data
  const useStateSpans = $derived(basalDeliverySpans.length > 0);

  // Track positions
  const trackBottom = $derived(trackTop + trackHeight);

  // Helper: convert a pixel Y position to the glucose data domain value
  const pixelToGlucoseDomain = $derived(
    (pixelY: number) => glucoseYMax * (1 - pixelY / chartHeight)
  );

  // Basal scale: rate -> glucose domain value
  const basalScale = $derived((rate: number) => {
    const pixelY = trackTop + (rate / maxBasalRate) * trackHeight;
    return pixelToGlucoseDomain(pixelY);
  });

  const basalZero = $derived(pixelToGlucoseDomain(trackTop));

  // D3 scale for basal Axis (maps rate -> pixel Y directly)
  const basalAxisScale = $derived(
    scaleLinear().domain([0, maxBasalRate]).range([trackTop, trackBottom])
  );
</script>

<!-- Pump mode background bands (render first, behind everything else) -->
{#each pumpModeSpans as span (span.id)}
  {@const spanXPos = context.xScale(span.displayStart)}
  <AnnotationRange
    x={[span.displayStart.getTime(), span.displayEnd.getTime()]}
    y={[basalScale(maxBasalRate), basalZero]}
    fill={span.color}
    class="opacity-20"
  />
  <!-- Pump mode icon at the start of each span -->
  <Group x={spanXPos} y={context.yScale(basalScale(maxBasalRate) + 6)}>
    <foreignObject x="2" y="-8" width="16" height="16">
      <div class="flex items-center justify-center w-full h-full">
        <PumpModeIcon state={span.state} size={12} color={span.color} />
      </div>
    </foreignObject>
  </Group>
{/each}

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
    x={(d) => d.time}
    y={(d) => basalScale(d.rate)}
    curve={curveStepAfter}
    class="stroke-muted-foreground/50 stroke-1 fill-none"
    stroke-dasharray="4,4"
  />
{/if}

<!-- Basal axis on right -->
{#if showAxis}
  <Axis
    placement="right"
    scale={basalAxisScale}
    ticks={2}
    tickLabelProps={{
      class: "text-[9px] fill-muted-foreground",
    }}
  />
{/if}

<!-- Basal track label -->
{#if showLabel}
  <Text
    x={4}
    y={trackTop + 12}
    class="text-[8px] fill-muted-foreground font-medium"
  >
    BASAL
  </Text>
{/if}

<!-- Effective basal area - StateSpans-based rendering -->
{#if useStateSpans}
  <!-- Render scheduled basal as background layer -->
  {#each scheduledDeliverySpans as span (span.id)}
    <AnnotationRange
      x={[span.displayStart.getTime(), span.displayEnd.getTime()]}
      y={[basalScale(span.rate), basalZero]}
      fill={span.color}
      class="opacity-60"
    />
  {/each}

  <!-- Render algorithm-adjusted basal (overlay on scheduled) -->
  {#each algorithmDeliverySpans as span (span.id)}
    <AnnotationRange
      x={[span.displayStart.getTime(), span.displayEnd.getTime()]}
      y={[basalScale(span.rate), basalZero]}
      fill={span.color}
      class="opacity-80"
    />
  {/each}

  <!-- Render manual temp basal (distinct overlay) -->
  {#each manualDeliverySpans as span (span.id)}
    <AnnotationRange
      x={[span.displayStart.getTime(), span.displayEnd.getTime()]}
      y={[basalScale(span.rate), basalZero]}
      fill={span.color}
      class="opacity-90"
    />
  {/each}

  <!-- Render suspended periods (show as minimal/zero) -->
  {#each suspendedDeliverySpans as span (span.id)}
    <AnnotationRange
      x={[span.displayStart.getTime(), span.displayEnd.getTime()]}
      y={[basalScale(maxBasalRate * 0.05), basalZero]}
      fill={span.color}
      class="opacity-50"
    />
  {/each}
{:else if basalData.length > 0}
  <!-- Legacy: Effective basal area (drips down from top of basal track) -->
  <Area
    data={basalData}
    x={(d) => d.time}
    y0={() => basalZero}
    y1={(d) => basalScale(d.rate)}
    curve={curveStepAfter}
    fill="var(--insulin-basal)"
    class="stroke-insulin stroke-1"
  />
{/if}
