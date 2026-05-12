<script lang="ts">
  import { ChartClipPath } from "layerchart";
  import { getChartContext } from "layerchart";
  import { getGlucoseChartContext } from "../chart-context.svelte";
  import DeviceEventMarker from "./DeviceEventMarker.svelte";

  interface Props {
    onMarkerClick?: (treatmentId: string) => void;
  }

  let { onMarkerClick }: Props = $props();

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  const visible = $derived(ctx.legend?.deviceEvents ?? true);
  const markers = $derived(ctx.engine.deviceEventMarkers);
  const medianY = $derived(ctx.layout.glucose.scale(ctx.engine.medianGlucose));
</script>

{#if visible}
  <ChartClipPath>
    {#each markers as marker, i (marker.treatmentId ?? `${marker.time.getTime()}-${i}`)}
      {@const xPos = chartCtx.xScale(marker.time)}
      {@const yPos = chartCtx.yScale(medianY)}
      <DeviceEventMarker
        {xPos}
        {yPos}
        eventType={marker.eventType}
        color={marker.color}
        treatmentId={marker.treatmentId ?? undefined}
        {onMarkerClick}
      />
    {/each}
  </ChartClipPath>
{/if}
