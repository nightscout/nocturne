<script lang="ts">
  import type { ComponentProps } from "svelte";
  import { createRealtimeStore } from "#lib/stores/realtime-store.svelte.js";
  import Page from "./+page.svelte";

  const {
    data,
    withRealtimeStore = false,
    currentReadingLoaded = false,
  }: {
    data: ComponentProps<typeof Page>["data"];
    /** Provide the layout's realtime store, which never loads here and so is never ready. */
    withRealtimeStore?: boolean;
    /** Mark the provided store's current reading as applied while the rest of its starting data is not. */
    currentReadingLoaded?: boolean;
  } = $props();

  // svelte-ignore state_referenced_locally
  if (withRealtimeStore) {
    const realtimeStore = createRealtimeStore({
      url: "",
      reconnectAttempts: 0,
      reconnectDelay: 0,
      maxReconnectDelay: 0,
      pingTimeout: 0,
      pingInterval: 0,
    });
    realtimeStore.currentReadingLoaded = currentReadingLoaded;
  }
</script>

<Page {data} />
