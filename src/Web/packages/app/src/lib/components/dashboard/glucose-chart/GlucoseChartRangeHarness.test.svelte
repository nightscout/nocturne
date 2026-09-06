<script lang="ts">
  // Mounts a real chart component so the props-to-engine wiring under test is the
  // production one. A harness that built the engine itself would pass whether or
  // not the component keeps its `dateRange` prop reactive.
  import { createRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import GlucoseChart from "./GlucoseChart.svelte";
  import GlucoseChartCard from "./GlucoseChartCard.svelte";

  interface Props {
    component: "chart" | "card";
    initialRange: { from: Date; to: Date };
    /** Hands the test the setter for the range the chart is mounted with. */
    onready: (setRange: (range: { from: Date; to: Date }) => void) => void;
  }

  let { component, initialRange, onready }: Props = $props();

  createRealtimeStore({
    url: "",
    reconnectAttempts: 0,
    reconnectDelay: 0,
    maxReconnectDelay: 0,
    pingTimeout: 0,
    pingInterval: 0,
  });

  // svelte-ignore state_referenced_locally
  let dateRange = $state(initialRange);
  // svelte-ignore state_referenced_locally
  onready((range) => (dateRange = range));
</script>

<div style="width: 600px; height: 400px;">
  {#if component === "card"}
    <GlucoseChartCard {dateRange} showPredictions={false} />
  {:else}
    <GlucoseChart
      {dateRange}
      enablePredictions={false}
      enableInspection={false}
    />
  {/if}
</div>
