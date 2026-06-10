<script lang="ts">
  import { getChartContext } from "layerchart";
  import { getGlucoseChartContext } from "../chart-context.svelte";
  import { getGlucoseColor } from "$lib/utils/chart-colors";

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  const markers = $derived(ctx.engine.bgCheckMarkers);
  const thresholds = $derived(ctx.engine.thresholds);
  const glucoseScale = $derived(ctx.layout.glucose.scale);
</script>

{#if markers.length > 0}
    {#each markers as marker (marker.treatmentId ?? `${marker.time.getTime()}`)}
      {@const xPos = chartCtx.xScale(marker.time)}
      {@const yPos = chartCtx.yScale(glucoseScale(marker.glucose))}
      {@const color = getGlucoseColor(marker.glucose, thresholds)}
      <!-- Diamond marker for fingerprick BG readings -->
      <g transform="translate({xPos}, {yPos})">
        <polygon
          points="0,-7 7,0 0,7 -7,0"
          fill={color}
          class="stroke-background"
          stroke-width="2"
        />
      </g>
    {/each}
{/if}
