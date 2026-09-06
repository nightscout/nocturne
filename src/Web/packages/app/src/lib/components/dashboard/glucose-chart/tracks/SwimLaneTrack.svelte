<script lang="ts">
  import { ChartClipPath, getChartContext } from "layerchart";
  import { PumpModeIcon, ActivityCategoryIcon } from "$lib/components/icons";
  import { getGlucoseChartContext } from "../chart-context.svelte";

  // Native SVG throughout: every span already carries pre-scaled pixel
  // coordinates, and layerchart marks each call registerMark() on mount, so one
  // <Rect>/<Text> per span cost O(N^2) across the chart's mark deriveds. Native
  // <rect>/<text>/<g> register nothing while rendering identically.
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
    <rect
      x={0}
      y={lane.top}
      width={chartCtx.width}
      height={lane.bottom - lane.top}
      fill="var(--muted)"
      class="opacity-20"
    />
    <!-- Lane label -->
    <text
      x={4}
      y={lane.top + (lane.bottom - lane.top) / 2 + 3}
      dy="-0.355em"
      class="text-[7px] fill-muted-foreground font-medium"
    >
      MODE
    </text>
    <!-- Pump mode spans -->
    {#each pumpModeSpans as span (span.id)}
      {@const spanXPos = chartCtx.xScale(span.displayStart)}
      <rect
        x={spanXPos}
        y={lane.top + 1}
        width={chartCtx.xScale(span.displayEnd) - spanXPos}
        height={lane.bottom - lane.top - 2}
        fill={span.color}
        class="opacity-60"
        rx="2"
      />
      <!-- Icon at start of span -->
      <g transform="translate({spanXPos}, {lane.top + (lane.bottom - lane.top) / 2})">
        <foreignObject x="2" y="-6" width="12" height="12">
          <div class="flex items-center justify-center w-full h-full">
            <PumpModeIcon state={span.state ?? ""} size={10} color={span.color} />
          </div>
        </foreignObject>
      </g>
    {/each}
  </ChartClipPath>
{/if}

<!-- Override Swim Lane -->
{#if swimLanePositions.override.visible}
  {@const lane = swimLanePositions.override}
  <ChartClipPath>
    <!-- Lane background -->
    <rect
      x={0}
      y={lane.top}
      width={chartCtx.width}
      height={lane.bottom - lane.top}
      fill="var(--muted)"
      class="opacity-20"
    />
    <!-- Lane label -->
    <text
      x={4}
      y={lane.top + (lane.bottom - lane.top) / 2 + 3}
      dy="-0.355em"
      class="text-[7px] fill-muted-foreground font-medium"
    >
      OVERRIDE
    </text>
    <!-- Override spans -->
    {#each overrideSpans as span (span.id)}
      {@const spanXPos = chartCtx.xScale(span.displayStart)}
      <rect
        x={spanXPos}
        y={lane.top + 1}
        width={chartCtx.xScale(span.displayEnd) - spanXPos}
        height={lane.bottom - lane.top - 2}
        fill={span.color}
        class="opacity-50"
        rx="2"
      />
      <!-- State label -->
      <text
        x={spanXPos + 4}
        y={lane.top + (lane.bottom - lane.top) / 2 + 3}
        dy="-0.355em"
        class="text-[6px] fill-foreground font-medium"
      >
        {span.state}
      </text>
    {/each}
  </ChartClipPath>
{/if}

<!-- Profile Swim Lane -->
{#if swimLanePositions.profile.visible}
  {@const lane = swimLanePositions.profile}
  <ChartClipPath>
    <!-- Lane background -->
    <rect
      x={0}
      y={lane.top}
      width={chartCtx.width}
      height={lane.bottom - lane.top}
      fill="var(--muted)"
      class="opacity-20"
    />
    <!-- Lane label -->
    <text
      x={4}
      y={lane.top + (lane.bottom - lane.top) / 2 + 3}
      dy="-0.355em"
      class="text-[7px] fill-muted-foreground font-medium"
    >
      PROFILE
    </text>
    <!-- Profile spans -->
    {#each profileSpans as span (span.id)}
      {@const spanXPos = chartCtx.xScale(span.displayStart)}
      <rect
        x={spanXPos}
        y={lane.top + 1}
        width={chartCtx.xScale(span.displayEnd) - spanXPos}
        height={lane.bottom - lane.top - 2}
        fill={span.color}
        class="opacity-30"
        rx="2"
      />
      <!-- Profile name label -->
      <text
        x={spanXPos + 4}
        y={lane.top + (lane.bottom - lane.top) / 2 + 3}
        dy="-0.355em"
        class="text-[6px] fill-foreground font-medium"
      >
        {span.profileName}
      </text>
    {/each}
  </ChartClipPath>
{/if}

<!-- Activity Swim Lane (Sleep, Exercise, Illness, Travel - all in one lane) -->
{#if swimLanePositions.activity?.visible}
  {@const lane = swimLanePositions.activity}
  <ChartClipPath>
    <!-- Lane background -->
    <rect
      x={0}
      y={lane.top}
      width={chartCtx.width}
      height={lane.bottom - lane.top}
      fill="var(--muted)"
      class="opacity-10"
    />
    <!-- Lane label -->
    <text
      x={4}
      y={lane.top + (lane.bottom - lane.top) / 2 + 3}
      dy="-0.355em"
      class="text-[7px] fill-muted-foreground font-medium"
    >
      ACTIVITY
    </text>
    <!-- All activity spans rendered in the same lane -->
    {#each activitySpans as span (span.id)}
      {@const spanXPos = chartCtx.xScale(span.displayStart)}
      <rect
        x={spanXPos}
        y={lane.top + 1}
        width={chartCtx.xScale(span.displayEnd) - spanXPos}
        height={lane.bottom - lane.top - 2}
        fill={span.color}
        class="opacity-50"
        rx="2"
      />
      <!-- Icon at start -->
      <g transform="translate({spanXPos}, {lane.top + (lane.bottom - lane.top) / 2})">
        <foreignObject x="2" y="-6" width="12" height="12">
          <div class="flex items-center justify-center w-full h-full">
            <ActivityCategoryIcon kind={span.kind} category={span.category} size={10} color={span.color} />
          </div>
        </foreignObject>
      </g>
    {/each}
  </ChartClipPath>
{/if}
