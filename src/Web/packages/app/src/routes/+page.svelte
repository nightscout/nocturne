<script lang="ts">
  import {
    CurrentBGDisplay,
    GlucoseChartCard,
    RecentEntriesCard,
    RecentTreatmentsCard,
    WidgetGrid,
  } from "$lib/components/dashboard";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { getSettingsStore } from "$lib/stores/settings-store.svelte";
  import {
    WidgetId,
    WidgetPlacement,
  } from "$lib/api/generated/nocturne-api-client";
  import {
    isWidgetEnabled,
    getEnabledWidgetsByPlacement,
  } from "$lib/types/dashboard-widgets";

  const realtimeStore = getRealtimeStore();
  const settingsStore = getSettingsStore();

  // Get widgets array from settings
  const widgets = $derived(settingsStore.features?.widgets);

  // Helper to check if a main section is enabled
  const isMainEnabled = (id: (typeof WidgetId)[keyof typeof WidgetId]) =>
    isWidgetEnabled(widgets, id);

  // Get enabled top widgets for the widget grid
  const topWidgets = $derived(
    getEnabledWidgetsByPlacement(widgets, WidgetPlacement.Top)
  );

  // Get focusHours setting for chart default time range
  const focusHours = $derived(settingsStore.features?.display?.focusHours ?? 3);

  // Algorithm prediction settings - controls whether predictions are calculated
  const predictionEnabled = $derived(
    settingsStore.algorithm?.prediction?.enabled ?? true
  );
</script>

<div class="@container p-3 @md:p-6 space-y-3 @md:space-y-6">
  <CurrentBGDisplay />

  {#if isMainEnabled(WidgetId.Statistics)}
    <WidgetGrid widgets={topWidgets} maxWidgets={3} />
  {/if}

  {#if isMainEnabled(WidgetId.GlucoseChart)}
    <GlucoseChartCard
      entries={realtimeStore.entries}
      treatments={realtimeStore.treatments}
      showPredictions={isMainEnabled(WidgetId.Predictions) && predictionEnabled}
      defaultFocusHours={focusHours}
    />
  {/if}

  {#if isMainEnabled(WidgetId.DailyStats)}
    <RecentEntriesCard />
  {/if}

  {#if isMainEnabled(WidgetId.Treatments)}
    <RecentTreatmentsCard />
  {/if}
</div>
