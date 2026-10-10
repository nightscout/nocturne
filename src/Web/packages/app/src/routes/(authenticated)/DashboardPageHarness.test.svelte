<script lang="ts">
  import { untrack, type ComponentProps } from "svelte";
  import type { FeatureSettings } from "$lib/api";
  import { createSettingsStore } from "$lib/stores/settings-store.svelte";
  import { createRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import Page from "./+page.svelte";

  const {
    features,
    data,
    withRealtimeStore = false,
  }: {
    features: FeatureSettings;
    data: ComponentProps<typeof Page>["data"];
    /** Provide the layout's realtime store, which never loads here and so is never ready. */
    withRealtimeStore?: boolean;
  } = $props();

  // svelte-ignore state_referenced_locally
  if (withRealtimeStore) {
    createRealtimeStore({
      url: "",
      reconnectAttempts: 0,
      reconnectDelay: 0,
      maxReconnectDelay: 0,
      pingTimeout: 0,
      pingInterval: 0,
    });
  }

  // The page reads tenant settings out of context; the layout owns the real one.
  const store = createSettingsStore(false);
  untrack(() => {
    store.features = features;
  });
</script>

<Page {data} />
