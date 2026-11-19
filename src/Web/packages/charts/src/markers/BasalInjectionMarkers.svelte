<script lang="ts">
  import { ChartClipPath } from "layerchart";
  import { getChartContext } from "layerchart";
  import { getGlucoseChartContext } from "../context.svelte.js";
  import BasalInjectionMarker from "./BasalInjectionMarker.svelte";
  import { markerKey } from "../keys.js";

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  const visible = $derived(ctx.legend?.basalInjections ?? true);
  const markers = $derived(ctx.view.basalInjectionMarkers);
  const basalTop = $derived(ctx.layout.basal?.top ?? 0);
</script>

{#if visible}
  <ChartClipPath>
    {#each markers as marker, i (markerKey(marker.id, marker.time, i))}
      {@const xPos = chartCtx.xScale(marker.time)}
      <BasalInjectionMarker
        {xPos}
        lineTop={basalTop + 20}
        lineBottom={chartCtx.height}
        units={marker.units}
        insulinName={marker.insulinName ?? undefined}
      />
    {/each}
  </ChartClipPath>
{/if}
