<script lang="ts">
  // Mounts the live renderer with its sources in context, the way the public clock route does,
  // so the assertions cover the renderer the anonymous viewer gets.
  import type { ClockFaceConfig } from "#lib/api/index.js";
  import {
    setClockGlucoseSource,
    type ClockGlucoseSource,
  } from "#lib/stores/realtime-store.svelte.js";
  import { setClockFaceDataSource, type ClockFaceDataSource } from "./clock-face-data.svelte";
  import ClockFaceRenderer from "./ClockFaceRenderer.svelte";

  interface Props {
    config: ClockFaceConfig;
    glucose: ClockGlucoseSource;
    faceData?: ClockFaceDataSource;
    showCharts?: boolean;
  }

  let { config, glucose, faceData, showCharts = false }: Props = $props();

  // svelte-ignore state_referenced_locally
  setClockGlucoseSource(glucose);
  // svelte-ignore state_referenced_locally
  if (faceData) setClockFaceDataSource(faceData);
</script>

<ClockFaceRenderer {config} {showCharts} />
