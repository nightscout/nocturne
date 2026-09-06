<script lang="ts">
  // Mounts the live renderer with a source in context, the way the public clock
  // route does, so the assertions cover the renderer the anonymous viewer gets.
  import type { ClockFaceConfig } from "$lib/api";
  import {
    setClockGlucoseSource,
    type ClockGlucoseSource,
  } from "$lib/stores/realtime-store.svelte";
  import ClockFaceRenderer from "./ClockFaceRenderer.svelte";

  interface Props {
    config: ClockFaceConfig;
    glucose: ClockGlucoseSource;
  }

  let { config, glucose }: Props = $props();

  // svelte-ignore state_referenced_locally
  setClockGlucoseSource(glucose);
</script>

<ClockFaceRenderer {config} showCharts={false} loadTrackerDefinitions={false} />
