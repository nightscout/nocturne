<script lang="ts">
  import { ChartClipPath, Group, Rect, getChartContext } from "layerchart";
  import { PumpModeIcon, ActivityCategoryIcon } from "#lib/components/icons/index.js";
  import { getGlucoseChartContext, markerInk } from "../chart-context.svelte";
  import TrackLabel from "./TrackLabel.svelte";
  import { markerKey } from "#lib/utils/chart-data-merge.js";

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  const swimLanePositions = $derived(ctx.layout.swimLanes);
  const pumpModeSpans = $derived(ctx.engine.displayPumpModeSpans);
  const overrideSpans = $derived(ctx.engine.displayOverrideSpans);
  const profileSpans = $derived(ctx.engine.displayProfileSpans);
  const activitySpans = $derived(ctx.engine.displayActivitySpans);
</script>

<!-- Pump Mode Swim Lane -->
{#if swimLanePositions.pumpMode.visible}
  {@const lane = swimLanePositions.pumpMode}
  <ChartClipPath>
    <!-- Lane background -->
    <Rect
      x={0}
      y={lane.top}
      width={chartCtx.width}
      height={lane.bottom - lane.top}
      fill="var(--muted)"
      class="opacity-20"
    />
    <!-- Pump mode spans -->
    {#each pumpModeSpans as span, i (markerKey(span.id, span.startTime, i))}
      {@const spanXPos = chartCtx.xScale(span.displayStart)}
      <Rect
        x={spanXPos}
        y={lane.top + 1}
        width={chartCtx.xScale(span.displayEnd) - spanXPos}
        height={lane.bottom - lane.top - 2}
        fill={span.color}
        class="opacity-60"
        rx={2}
      />
      <!-- Icon at start of span -->
      <Group x={spanXPos} y={lane.top + (lane.bottom - lane.top) / 2}>
        <foreignObject x="2" y="-6" width="12" height="12">
          <div class="flex items-center justify-center w-full h-full">
            <PumpModeIcon state={span.state ?? ""} size={10} color={markerInk(ctx, span.color)} />
          </div>
        </foreignObject>
      </Group>
    {/each}
  </ChartClipPath>
  <TrackLabel label="Mode" top={lane.top} bottom={lane.bottom} lane />
{/if}

<!-- Override Swim Lane -->
{#if swimLanePositions.override.visible}
  {@const lane = swimLanePositions.override}
  <ChartClipPath>
    <!-- Lane background -->
    <Rect
      x={0}
      y={lane.top}
      width={chartCtx.width}
      height={lane.bottom - lane.top}
      fill="var(--muted)"
      class="opacity-20"
    />
    <!-- Override spans -->
    {#each overrideSpans as span, i (markerKey(span.id, span.startTime, i))}
      {@const spanXPos = chartCtx.xScale(span.displayStart)}
      <Rect
        x={spanXPos}
        y={lane.top + 1}
        width={chartCtx.xScale(span.displayEnd) - spanXPos}
        height={lane.bottom - lane.top - 2}
        fill={span.color}
        class="opacity-50"
        rx={2}
      />
      <!-- State label -->
      <text
        x={spanXPos + 4}
        y={lane.top + (lane.bottom - lane.top) / 2 + 3}
        dy="-0.355em"
        class="text-4xs fill-foreground font-medium"
      >
        {span.state}
      </text>
    {/each}
  </ChartClipPath>
  <TrackLabel label="Override" top={lane.top} bottom={lane.bottom} lane />
{/if}

<!-- Profile Swim Lane -->
{#if swimLanePositions.profile.visible}
  {@const lane = swimLanePositions.profile}
  <ChartClipPath>
    <!-- Lane background -->
    <Rect
      x={0}
      y={lane.top}
      width={chartCtx.width}
      height={lane.bottom - lane.top}
      fill="var(--muted)"
      class="opacity-20"
    />
    <!-- Profile spans -->
    {#each profileSpans as span, i (markerKey(span.id, span.startTime, i))}
      {@const spanXPos = chartCtx.xScale(span.displayStart)}
      <Rect
        x={spanXPos}
        y={lane.top + 1}
        width={chartCtx.xScale(span.displayEnd) - spanXPos}
        height={lane.bottom - lane.top - 2}
        fill={span.color}
        class="opacity-30"
        rx={2}
      />
      <!-- Profile name label -->
      <text
        x={spanXPos + 4}
        y={lane.top + (lane.bottom - lane.top) / 2 + 3}
        dy="-0.355em"
        class="text-4xs fill-foreground font-medium"
      >
        {span.profileName}
      </text>
    {/each}
  </ChartClipPath>
  <TrackLabel label="Profile" top={lane.top} bottom={lane.bottom} lane />
{/if}

<!-- Activity Swim Lane (Sleep, Exercise, Illness, Travel - all in one lane) -->
{#if swimLanePositions.activity?.visible}
  {@const lane = swimLanePositions.activity}
  <ChartClipPath>
    <!-- Lane background -->
    <Rect
      x={0}
      y={lane.top}
      width={chartCtx.width}
      height={lane.bottom - lane.top}
      fill="var(--muted)"
      class="opacity-10"
    />
    <!-- All activity spans rendered in the same lane -->
    {#each activitySpans as span, i (markerKey(span.id, span.startTime, i))}
      {@const spanXPos = chartCtx.xScale(span.displayStart)}
      <Rect
        x={spanXPos}
        y={lane.top + 1}
        width={chartCtx.xScale(span.displayEnd) - spanXPos}
        height={lane.bottom - lane.top - 2}
        fill={span.color}
        class="opacity-50"
        rx={2}
      />
      <!-- Icon at start -->
      <Group x={spanXPos} y={lane.top + (lane.bottom - lane.top) / 2}>
        <foreignObject x="2" y="-6" width="12" height="12">
          <div class="flex items-center justify-center w-full h-full">
            <ActivityCategoryIcon kind={span.kind} category={span.category} size={10} color={markerInk(ctx, span.color)} />
          </div>
        </foreignObject>
      </Group>
    {/each}
  </ChartClipPath>
  <TrackLabel label="Activity" top={lane.top} bottom={lane.bottom} lane />
{/if}
