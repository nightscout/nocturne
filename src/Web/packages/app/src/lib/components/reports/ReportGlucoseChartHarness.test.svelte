<script lang="ts">
  // Mounts the real chart with a realtime store, which the chart-data engine reads.
  import { createRealtimeStore } from "#lib/stores/realtime-store.svelte.js";
  import ReportGlucoseChart from "./ReportGlucoseChart.svelte";

  let { from, to, insulin }: { from: Date; to: Date; insulin: boolean } = $props();

  createRealtimeStore({
    url: "",
    reconnectAttempts: 0,
    reconnectDelay: 0,
    maxReconnectDelay: 0,
    pingTimeout: 0,
    pingInterval: 0,
  });
</script>

<div class="w-[600px]">
  <ReportGlucoseChart {from} {to} {insulin}>
    {#snippet under()}
      <rect data-testid="under" />
    {/snippet}
    {#snippet over()}
      <rect data-testid="over" />
    {/snippet}
  </ReportGlucoseChart>
</div>
