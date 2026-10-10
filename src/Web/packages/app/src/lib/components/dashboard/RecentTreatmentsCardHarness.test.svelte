<script lang="ts">
  import { createRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import type { EntryRecord } from "$lib/constants/entry-categories";
  import RecentTreatmentsCard from "./RecentTreatmentsCard.svelte";

  let { entries }: { entries: EntryRecord[] } = $props();

  // The card and the entry dialog read the store from context. Never initialized, so no socket opens.
  createRealtimeStore({
    url: "",
    reconnectAttempts: 0,
    reconnectDelay: 0,
    maxReconnectDelay: 0,
    pingTimeout: 0,
    pingInterval: 0,
  });
</script>

<RecentTreatmentsCard {entries} />
