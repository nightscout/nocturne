<script lang="ts">
  // Mounts the live renderer with a source in context, the way the public clock
  // route does (plus a realtime store when charts are on), so the assertions cover the renderer the anonymous viewer gets.
  import type { ClockFaceConfig } from "#lib/api/index.js";
  import {
    createRealtimeStore,
    setClockGlucoseSource,
    type ClockGlucoseSource,
  } from "#lib/stores/realtime-store.svelte.js";
  import ClockFaceRenderer from "./ClockFaceRenderer.svelte";

  interface Props {
    config: ClockFaceConfig;
    glucose: ClockGlucoseSource;
    showCharts?: boolean;
  }

  let { config, glucose, showCharts = false }: Props = $props();

  // svelte-ignore state_referenced_locally
  setClockGlucoseSource(glucose);

  // The chart's engine reads the app's realtime store, which the public route does not have.
  // svelte-ignore state_referenced_locally
  if (showCharts) {
    createRealtimeStore({
      url: "",
      reconnectAttempts: 0,
      reconnectDelay: 0,
      maxReconnectDelay: 0,
      pingTimeout: 0,
      pingInterval: 0,
    });
  }
</script>

<ClockFaceRenderer {config} {showCharts} loadTrackerDefinitions={false} />
