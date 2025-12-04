<script lang="ts">
  import {
    CurrentBGDisplay,
    BGStatisticsCards,
    GlucoseChartCard,
    RecentEntriesCard,
    RecentTreatmentsCard,
  } from "$lib/components/dashboard";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";

  const realtimeStore = getRealtimeStore();
</script>

<div class="p-6 space-y-6">
  <CurrentBGDisplay />

  <BGStatisticsCards />

  <GlucoseChartCard
    entries={realtimeStore.entries}
    treatments={realtimeStore.treatments}
  />

  <RecentEntriesCard />

  <RecentTreatmentsCard />
</div>
