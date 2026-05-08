<script lang="ts">
  import { Rect, Text, Group, getChartContext } from "layerchart";
  import { PumpModeIcon, ActivityCategoryIcon } from "$lib/components/icons";
  import { getGlucoseChartContext } from "../chart-context.svelte";

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
  <!-- Lane background -->
  <Rect
    x={0}
    y={lane.top}
    width={chartCtx.width}
    height={lane.bottom - lane.top}
    fill="var(--muted)"
    class="opacity-20"
  />
  <!-- Lane label -->
  <Text
    x={4}
    y={lane.top + (lane.bottom - lane.top) / 2 + 3}
    class="text-[7px] fill-muted-foreground font-medium"
  >
    MODE
  </Text>
  <!-- Pump mode spans -->
  {#each pumpModeSpans as span (span.id)}
    {@const spanXPos = chartCtx.xScale(span.displayStart)}
    <Rect
      x={spanXPos}
      y={lane.top + 1}
      width={chartCtx.xScale(span.displayEnd) - spanXPos}
      height={lane.bottom - lane.top - 2}
      fill={span.color}
      class="opacity-60"
      rx="2"
    />
    <!-- Icon at start of span -->
    <Group x={spanXPos} y={lane.top + (lane.bottom - lane.top) / 2}>
      <foreignObject x="2" y="-6" width="12" height="12">
        <div class="flex items-center justify-center w-full h-full">
          <PumpModeIcon state={span.state ?? ""} size={10} color={span.color} />
        </div>
      </foreignObject>
    </Group>
  {/each}
{/if}

<!-- Override Swim Lane -->
{#if swimLanePositions.override.visible}
  {@const lane = swimLanePositions.override}
  <!-- Lane background -->
  <Rect
    x={0}
    y={lane.top}
    width={chartCtx.width}
    height={lane.bottom - lane.top}
    fill="var(--muted)"
    class="opacity-20"
  />
  <!-- Lane label -->
  <Text
    x={4}
    y={lane.top + (lane.bottom - lane.top) / 2 + 3}
    class="text-[7px] fill-muted-foreground font-medium"
  >
    OVERRIDE
  </Text>
  <!-- Override spans -->
  {#each overrideSpans as span (span.id)}
    {@const spanXPos = chartCtx.xScale(span.displayStart)}
    <Rect
      x={spanXPos}
      y={lane.top + 1}
      width={chartCtx.xScale(span.displayEnd) - spanXPos}
      height={lane.bottom - lane.top - 2}
      fill={span.color}
      class="opacity-50"
      rx="2"
    />
    <!-- State label -->
    <Text
      x={spanXPos + 4}
      y={lane.top + (lane.bottom - lane.top) / 2 + 3}
      class="text-[6px] fill-foreground font-medium"
    >
      {span.state}
    </Text>
  {/each}
{/if}

<!-- Profile Swim Lane -->
{#if swimLanePositions.profile.visible}
  {@const lane = swimLanePositions.profile}
  <!-- Lane background -->
  <Rect
    x={0}
    y={lane.top}
    width={chartCtx.width}
    height={lane.bottom - lane.top}
    fill="var(--muted)"
    class="opacity-20"
  />
  <!-- Lane label -->
  <Text
    x={4}
    y={lane.top + (lane.bottom - lane.top) / 2 + 3}
    class="text-[7px] fill-muted-foreground font-medium"
  >
    PROFILE
  </Text>
  <!-- Profile spans -->
  {#each profileSpans as span (span.id)}
    {@const spanXPos = chartCtx.xScale(span.displayStart)}
    <Rect
      x={spanXPos}
      y={lane.top + 1}
      width={chartCtx.xScale(span.displayEnd) - spanXPos}
      height={lane.bottom - lane.top - 2}
      fill={span.color}
      class="opacity-30"
      rx="2"
    />
    <!-- Profile name label -->
    <Text
      x={spanXPos + 4}
      y={lane.top + (lane.bottom - lane.top) / 2 + 3}
      class="text-[6px] fill-foreground font-medium"
    >
      {span.profileName}
    </Text>
  {/each}
{/if}

<!-- Activity Swim Lane (Sleep, Exercise, Illness, Travel - all in one lane) -->
{#if swimLanePositions.activity?.visible}
  {@const lane = swimLanePositions.activity}
  <!-- Lane background -->
  <Rect
    x={0}
    y={lane.top}
    width={chartCtx.width}
    height={lane.bottom - lane.top}
    fill="var(--muted)"
    class="opacity-10"
  />
  <!-- Lane label -->
  <Text
    x={4}
    y={lane.top + (lane.bottom - lane.top) / 2 + 3}
    class="text-[7px] fill-muted-foreground font-medium"
  >
    ACTIVITY
  </Text>
  <!-- All activity spans rendered in the same lane -->
  {#each activitySpans as span (span.id)}
    {@const spanXPos = chartCtx.xScale(span.displayStart)}
    <Rect
      x={spanXPos}
      y={lane.top + 1}
      width={chartCtx.xScale(span.displayEnd) - spanXPos}
      height={lane.bottom - lane.top - 2}
      fill={span.color}
      class="opacity-50"
      rx="2"
    />
    <!-- Icon at start -->
    <Group x={spanXPos} y={lane.top + (lane.bottom - lane.top) / 2}>
      <foreignObject x="2" y="-6" width="12" height="12">
        <div class="flex items-center justify-center w-full h-full">
          <ActivityCategoryIcon category={span.category} size={10} color={span.color} />
        </div>
      </foreignObject>
    </Group>
  {/each}
{/if}
