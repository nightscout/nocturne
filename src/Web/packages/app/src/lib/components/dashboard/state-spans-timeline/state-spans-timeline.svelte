<script lang="ts">
  import { Chart, Svg, Axis, Rect, Text, Group } from "layerchart";
  import { scaleTime, scaleLinear } from "d3-scale";
  import { PumpModeIcon, ActivityCategoryIcon } from "$lib/components/icons";
  import { BasalRateTrack } from "$lib/components/charts";
  import type { ProcessedSpan } from "../../../../routes/time-spans/data.remote";
  import { BasalDeliveryOrigin } from "$lib/api";

  interface BasalDeliveryChartData {
    id: string;
    startTime: Date;
    endTime: Date | null;
    rate: number;
    origin: BasalDeliveryOrigin;
    source?: string;
    color: string;
  }

  interface TrackConfig {
    key: string;
    label: string;
    spans: ProcessedSpan[];
    visible: boolean;
  }

  interface Props {
    pumpModeSpans: ProcessedSpan[];
    profileSpans: ProcessedSpan[];
    tempBasalSpans: ProcessedSpan[];
    overrideSpans: ProcessedSpan[];
    activitySpans: ProcessedSpan[];
    dateRange: { from: Date; to: Date };
    showPumpModes: boolean;
    showProfiles: boolean;
    showTempBasals: boolean;
    showOverrides: boolean;
    showActivities: boolean;
  }

  let {
    pumpModeSpans,
    profileSpans,
    tempBasalSpans,
    overrideSpans,
    activitySpans,
    dateRange,
    showPumpModes,
    showProfiles,
    showTempBasals,
    showOverrides,
    showActivities,
  }: Props = $props();

  // Build track configuration based on visibility (excluding basal which is handled separately)
  const standardTracks = $derived.by(() => {
    const allTracks: TrackConfig[] = [
      { key: "pumpMode", label: "PUMP MODE", spans: pumpModeSpans, visible: showPumpModes },
      { key: "profile", label: "PROFILE", spans: profileSpans, visible: showProfiles },
      { key: "override", label: "OVERRIDE", spans: overrideSpans, visible: showOverrides },
      { key: "activity", label: "ACTIVITY", spans: activitySpans, visible: showActivities },
    ];
    return allTracks.filter((t) => t.visible);
  });

  // Track height in pixels
  const TRACK_HEIGHT = 40;
  const BASAL_TRACK_HEIGHT = 60;
  const LABEL_WIDTH = 90;

  // Calculate total chart height based on visible tracks
  const chartHeight = $derived.by(() => {
    let height = standardTracks.length * TRACK_HEIGHT + 30;
    if (showTempBasals) {
      height += BASAL_TRACK_HEIGHT;
    }
    return Math.max(height, 100);
  });

  // Calculate max basal rate for y-scale
  const maxBasalRate = $derived.by(() => {
    if (!tempBasalSpans || tempBasalSpans.length === 0) return 2;
    const rates = tempBasalSpans.map((s) => s.rate ?? 0);
    const max = Math.max(...rates);
    return Math.max(max * 1.2, 0.5);
  });

  // Calculate basal track position
  const basalTrackTop = $derived(standardTracks.length * TRACK_HEIGHT + 5);

  // Convert ProcessedSpan to BasalDeliveryChartData format for BasalRateTrack
  const basalDeliverySpans = $derived.by((): (BasalDeliveryChartData & { displayStart: Date; displayEnd: Date })[] => {
    if (!tempBasalSpans) return [];
    return tempBasalSpans.map((span) => ({
      id: span.id,
      startTime: span.startTime,
      endTime: span.endTime,
      displayStart: span.startTime,
      displayEnd: span.endTime,
      rate: span.rate ?? 0,
      origin: span.origin ?? BasalDeliveryOrigin.Scheduled,
      source: typeof span.metadata?.source === "string" ? span.metadata.source : undefined,
      color: span.color,
    }));
  });

  // Dummy glucose scale values for BasalRateTrack (it needs these for internal calculations)
  const glucoseYMax = $derived(chartHeight);

  // Hovered span for tooltip
  let hoveredSpan = $state<ProcessedSpan | null>(null);
  let tooltipX = $state(0);
  let tooltipY = $state(0);

  // Format duration for tooltip
  function formatDuration(start: Date, end: Date): string {
    const ms = end.getTime() - start.getTime();
    const hours = Math.floor(ms / (1000 * 60 * 60));
    const minutes = Math.floor((ms % (1000 * 60 * 60)) / (1000 * 60));
    if (hours > 0) {
      return `${hours}h ${minutes}m`;
    }
    return `${minutes}m`;
  }

  // Format time for tooltip
  function formatTime(date: Date): string {
    return date.toLocaleString(undefined, {
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  }
</script>

<div class="relative w-full" style="height: {chartHeight}px;">
  {#if standardTracks.length === 0 && !showTempBasals}
    <div class="flex h-full items-center justify-center text-sm text-muted-foreground">
      No tracks selected. Enable at least one category above.
    </div>
  {:else}
    <Chart
      data={[]}
      xScale={scaleTime()}
      yScale={scaleLinear()}
      xDomain={[dateRange.from, dateRange.to]}
      yDomain={[0, chartHeight]}
      padding={{ top: 5, right: 10, bottom: 25, left: LABEL_WIDTH }}
    >
      {#snippet children({ context })}
        <Svg>
        <!-- Standard category tracks (non-basal) -->
        {#each standardTracks as track, i (track.key)}
          {@const yPos = i * TRACK_HEIGHT + 5}
          <!-- Track background -->
          <Rect
            x={context.xScale(dateRange.from)}
            y={yPos}
            width={context.xScale(dateRange.to) - context.xScale(dateRange.from)}
            height={TRACK_HEIGHT - 2}
            fill="var(--muted)"
            class="opacity-20"
          />
          <!-- Track label -->
          <Text
            x={-LABEL_WIDTH + 8}
            y={yPos + TRACK_HEIGHT / 2 + 4}
            class="text-[10px] fill-muted-foreground font-medium"
          >
            {track.label}
          </Text>

          <!-- Span bars for this track -->
          {#each track.spans as span (span.id)}
            {@const xStartPx = context.xScale(span.startTime)}
            {@const xEndPx = context.xScale(span.endTime)}
            <Rect
              x={xStartPx}
              y={yPos + 2}
              width={xEndPx - xStartPx}
              height={TRACK_HEIGHT - 6}
              fill={span.color}
              class="opacity-70 cursor-pointer transition-opacity hover:opacity-100"
              rx={3}
              onmouseenter={(e: MouseEvent) => {
                hoveredSpan = span;
                tooltipX = e.clientX;
                tooltipY = e.clientY;
              }}
              onmousemove={(e: MouseEvent) => {
                tooltipX = e.clientX;
                tooltipY = e.clientY;
              }}
              onmouseleave={() => {
                hoveredSpan = null;
              }}
            />
            <!-- Icon/label at start of span -->
            {#if track.key === "pumpMode"}
              <Group x={xStartPx} y={yPos + TRACK_HEIGHT / 2}>
                <foreignObject x={4} y={-8} width={16} height={16}>
                  <div class="flex items-center justify-center w-full h-full">
                    <PumpModeIcon state={span.state} size={14} color={span.color} />
                  </div>
                </foreignObject>
              </Group>
            {:else if track.key === "activity"}
              <Group x={xStartPx} y={yPos + TRACK_HEIGHT / 2}>
                <foreignObject x={4} y={-8} width={16} height={16}>
                  <div class="flex items-center justify-center w-full h-full">
                    <ActivityCategoryIcon category={span.category} size={14} color={span.color} />
                  </div>
                </foreignObject>
              </Group>
            {:else if track.key === "profile" && span.profileName}
              <Text
                x={xStartPx}
                y={yPos + TRACK_HEIGHT / 2 + 4}
                dx={6}
                class="text-[9px] fill-foreground font-medium pointer-events-none"
              >
                {span.profileName}
              </Text>
            {:else if track.key === "override"}
              <Text
                x={xStartPx}
                y={yPos + TRACK_HEIGHT / 2 + 4}
                dx={6}
                class="text-[9px] fill-foreground font-medium pointer-events-none"
              >
                {span.state}
              </Text>
            {/if}
          {/each}
        {/each}

        <!-- Basal delivery track using BasalRateTrack component -->
        {#if showTempBasals}
          <!-- Track background -->
          <Rect
            x={context.xScale(dateRange.from)}
            y={basalTrackTop}
            width={context.xScale(dateRange.to) - context.xScale(dateRange.from)}
            height={BASAL_TRACK_HEIGHT - 2}
            fill="var(--muted)"
            class="opacity-20"
          />

          <BasalRateTrack
            {maxBasalRate}
            {basalDeliverySpans}
            trackHeight={BASAL_TRACK_HEIGHT - 5}
            trackTop={basalTrackTop}
            chartHeight={chartHeight}
            glucoseYMax={glucoseYMax}
            context={{
              xScale: (date: Date) => context.xScale(date),
              yScale: (value: number) => context.yScale(value),
            }}
            showAxis={false}
            showLabel={true}
          />
        {/if}

        <!-- Time axis at bottom -->
        <Axis
          placement="bottom"
          rule
          tickLabelProps={{
            class: "text-[10px] fill-muted-foreground",
          }}
        />
        </Svg>
      {/snippet}
    </Chart>

    <!-- Custom tooltip -->
    {#if hoveredSpan}
      <div
        class="fixed z-50 bg-popover text-popover-foreground border rounded-md shadow-md px-3 py-2 text-sm pointer-events-none"
        style="left: {tooltipX + 12}px; top: {tooltipY - 10}px;"
      >
        <div class="font-medium">{hoveredSpan.profileName ?? hoveredSpan.state}</div>
        <div class="text-xs text-muted-foreground mt-1">
          <div>{formatTime(hoveredSpan.startTime)} - {formatTime(hoveredSpan.endTime)}</div>
          <div>Duration: {formatDuration(hoveredSpan.startTime, hoveredSpan.endTime)}</div>
        </div>
      </div>
    {/if}
  {/if}
</div>
