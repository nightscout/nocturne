<script lang="ts">
  import { ChartClipPath } from "layerchart";
  import { getChartContext } from "layerchart";
  import { getGlucoseChartContext } from "../chart-context.svelte";
  import BasalInjectionMarker from "./BasalInjectionMarker.svelte";

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  const visible = $derived(ctx.legend?.basalInjections ?? true);
  const markers = $derived(ctx.engine.basalInjectionMarkers);
  const basalTop = $derived(ctx.layout.basal?.top ?? 0);
</script>

{#if visible}
  <ChartClipPath>
    {#each markers as marker (marker.id)}
      {@const xPos = chartCtx.xScale(marker.time)}
      <BasalInjectionMarker
        {xPos}
        lineTop={basalTop + 20}
        lineBottom={chartCtx.height}
        units={marker.units}
        insulinName={marker.insulinName}
      />
    {/each}
  </ChartClipPath>
{/if}
