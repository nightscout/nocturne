<script lang="ts">
  import DeviceEventMarker from "./DeviceEventMarker.svelte";
  import { setGlucoseChartContext } from "../chart-context.svelte";
  import type { GlucoseChartContext } from "../chart-context.svelte";
  import { DeviceEventType } from "#lib/api/index.js";

  interface Props {
    isInferred?: boolean;
  }

  let { isInferred }: Props = $props();

  // The marker reads only `printing` off the chart context.
  setGlucoseChartContext({ printing: false } as GlucoseChartContext);
</script>

<svg width="100" height="100">
  <DeviceEventMarker
    xPos={50}
    yPos={50}
    eventType={DeviceEventType.SensorStart}
    color="var(--chart-device-event)"
    {isInferred}
  />
</svg>
