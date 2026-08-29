<script lang="ts">
  // Test-only harness: the engine reads the realtime store out of context and
  // registers effects, both of which need a real component lifecycle. Hands the
  // engine back to the test.
  import { createRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import type { Entry } from "$lib/websocket/types";
  import { createChartDataEngine } from "./chart-data-engine.svelte";
  import type {
    ChartDataEngine,
    ChartDataEngineOptions,
  } from "./chart-data-engine.svelte";

  interface Props {
    entries: Entry[];
    options: ChartDataEngineOptions;
    onengine: (engine: ChartDataEngine) => void;
  }

  let { entries, options, onengine }: Props = $props();

  const store = createRealtimeStore({
    url: "",
    reconnectAttempts: 0,
    reconnectDelay: 0,
    maxReconnectDelay: 0,
    pingTimeout: 0,
    pingInterval: 0,
  });
  // Read once, deliberately: each test mounts the harness with the props it wants
  // and never changes them, so there is nothing for a closure to track.
  // svelte-ignore state_referenced_locally
  store.entries = entries;

  // svelte-ignore state_referenced_locally
  onengine(createChartDataEngine(options));
</script>
