<script lang="ts">
  import { Group, getChartContext } from "layerchart";
  import { getGlucoseChartContext } from "../chart-context.svelte";
  import { getGlucoseColor } from "#lib/utils/chart-colors.js";
  import { markerKey } from "#lib/utils/chart-data-merge.js";

  /**
   * Outline each diamond in the foreground colour instead of cutting it out of the chart, for a
   * chart that draws highlighted stretches behind it, where the cut-out shows as a white edge.
   */
  let { outlined = false }: { outlined?: boolean } = $props();

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  const markers = $derived(ctx.engine.bgCheckMarkers);
  const thresholds = $derived(ctx.engine.thresholds);
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
          class={outlined ? "stroke-foreground" : "stroke-background"}
          stroke-width={outlined ? 1.5 : 2}
        />
      </Group>
    {/each}
{/if}
