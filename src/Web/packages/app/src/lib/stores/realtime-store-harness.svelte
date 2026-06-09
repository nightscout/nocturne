<script lang="ts">
  // Test-only harness: exercises createRealtimeStore inside a real component
  // lifecycle so setContext() is valid. Hands the created store back to the test.
  import { createRealtimeStore } from "./realtime-store.svelte";
  import type { RealtimeStore } from "./realtime-store.svelte";

  let { onstore }: { onstore: (store: RealtimeStore) => void } = $props();

  onstore(
    createRealtimeStore({
      url: "",
      reconnectAttempts: 0,
      reconnectDelay: 0,
      maxReconnectDelay: 0,
      pingTimeout: 0,
      pingInterval: 0,
    })
  );
</script>
