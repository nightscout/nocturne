<script lang="ts">
  import { ChartClipPath } from "layerchart";
  import { getChartContext } from "layerchart";
  import { getGlucoseChartContext } from "../context.svelte.js";
  import SystemEventMarker from "./SystemEventMarker.svelte";
  import { markerKey } from "../keys.js";

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  const visible = $derived(ctx.legend?.alarms ?? true);
  const events = $derived(ctx.view.displaySystemEvents);
  const lowY = $derived(ctx.layout.glucose.scale(ctx.view.thresholds.low * 0.8));
</script>

{#if visible}
  <ChartClipPath>
    {#each events as event, i (markerKey(event.id, event.time, i))}
      {@const xPos = chartCtx.xScale(event.time)}
      {@const yPos = chartCtx.yScale(lowY)}
      <SystemEventMarker
        {xPos}
        {yPos}
        eventType={event.eventType}
        color={event.color}
      />
    {/each}
  </ChartClipPath>
{/if}
