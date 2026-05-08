<script lang="ts">
  import { ChartClipPath } from "layerchart";
  import { getChartContext } from "layerchart";
  import { getGlucoseChartContext } from "../chart-context.svelte";
  import TrackerExpirationMarker from "./TrackerExpirationMarker.svelte";

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  const visible = $derived(ctx.legend?.scheduledTrackers ?? true);
  const markers = $derived(ctx.engine.trackerMarkers);
  const basalTop = $derived(ctx.layout.basal?.top ?? 0);
</script>

{#if visible}
  <ChartClipPath>
    {#each markers as marker (marker.id)}
      {@const xPos = chartCtx.xScale(marker.time)}
      <TrackerExpirationMarker
        {xPos}
        lineTop={basalTop + 20}
        lineBottom={chartCtx.height}
        basalTrackTop={basalTop}
        time={marker.time}
        category={marker.category}
        color={marker.color}
      />
    {/each}
  </ChartClipPath>
{/if}
