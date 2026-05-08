<script lang="ts">
  import { ChartClipPath } from "layerchart";
  import { getChartContext } from "layerchart";
  import { getGlucoseChartContext } from "../chart-context.svelte";
  import SystemEventMarker from "./SystemEventMarker.svelte";

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  const visible = $derived(ctx.legend?.alarms ?? true);
  const events = $derived(ctx.engine.systemEventMarkers);
  const lowY = $derived(ctx.layout.glucose.scale(ctx.engine.thresholds.low * 0.8));
</script>

{#if visible}
  <ChartClipPath>
    {#each events as event (event.id)}
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
