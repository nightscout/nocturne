<script lang="ts">
  import { ChartClipPath } from "layerchart";
  import { getChartContext } from "layerchart";
  import { getGlucoseChartContext } from "../context.svelte.js";
  import DeviceEventMarker from "./DeviceEventMarker.svelte";
  import { markerKey } from "../keys.js";

  interface Props {
    onMarkerClick?: (treatmentId: string) => void;
  }

  let { onMarkerClick }: Props = $props();

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  const visible = $derived(ctx.legend?.deviceEvents ?? true);
  const markers = $derived(ctx.view.deviceEventMarkers);
  const medianY = $derived(ctx.layout.glucose.scale(ctx.view.medianGlucose));
</script>

{#if visible}
  <ChartClipPath>
    {#each markers as marker, i (markerKey(marker.treatmentId, marker.time, i))}
      {@const xPos = chartCtx.xScale(marker.time)}
      {@const yPos = chartCtx.yScale(medianY)}
      <DeviceEventMarker
        {xPos}
        {yPos}
        eventType={marker.eventType}
        color={marker.color}
        treatmentId={marker.treatmentId ?? undefined}
        isInferred={marker.isInferred ?? false}
        {onMarkerClick}
      />
    {/each}
  </ChartClipPath>
{/if}
