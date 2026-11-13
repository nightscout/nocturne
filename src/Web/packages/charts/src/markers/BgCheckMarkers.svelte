<script lang="ts">
  import { Group, getChartContext } from "layerchart";
  import { getGlucoseChartContext } from "../context.svelte.js";
  import { getGlucoseColor } from "../colors.js";
  import { markerKey } from "../keys.js";

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  const markers = $derived(ctx.view.bgCheckMarkers);
  const thresholds = $derived(ctx.view.thresholds);
  const glucoseScale = $derived(ctx.layout.glucose.scale);
</script>

{#if markers.length > 0}
    {#each markers as marker, i (markerKey(marker.treatmentId, marker.time, i))}
      {@const xPos = chartCtx.xScale(marker.time)}
      {@const yPos = chartCtx.yScale(glucoseScale(marker.glucose))}
      {@const color = getGlucoseColor(marker.glucose, thresholds)}
      <!-- Diamond marker for fingerprick BG readings -->
      <Group x={xPos} y={yPos}>
        <polygon
          points="0,-7 7,0 0,7 -7,0"
          fill={color}
          class="stroke-background"
          stroke-width="2"
        />
      </Group>
    {/each}
{/if}
