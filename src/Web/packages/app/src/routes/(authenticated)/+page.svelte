<script lang="ts">
  import {
    CurrentBGDisplay,
    GlucoseChartCard,
    RecentEntriesCard,
    RecentTreatmentsCard,
    WidgetGrid,
  } from "$lib/components/dashboard";
  import { getSettingsStore } from "$lib/stores/settings-store.svelte";
  import { dashboardTopWidgets } from "$lib/stores/appearance-store.svelte";
  import { WidgetId } from "$lib/api/generated/nocturne-api-client";
  import { isWidgetEnabled } from "$lib/types/dashboard-widgets";
  import { coachmark } from "@nocturne/coach";
  import type { PageData } from "./$types";

  const { data }: { data: PageData } = $props();

  const settingsStore = getSettingsStore();

  // Get widgets array from settings (for main section visibility)
  const widgets = $derived(settingsStore.features?.widgets);

  // Helper to check if a main section is enabled
  const isMainEnabled = (id: (typeof WidgetId)[keyof typeof WidgetId]) =>
    isWidgetEnabled(widgets, id);

  // Get enabled top widgets from persisted appearance store
  const topWidgets = $derived(dashboardTopWidgets.current);

  // Get focusHours setting for chart default time range
  const focusHours = $derived(settingsStore.features?.display?.focusHours ?? 3);

  // Algorithm prediction settings - controls whether predictions are calculated
  const predictionEnabled = $derived(
    settingsStore.algorithm?.prediction?.enabled ?? true
  );
</script>

<div class="@container p-3 @md:p-6 space-y-3 @md:space-y-6">
  <div {@attach coachmark({
    key: "quick-tour.current-bg",
    title: "Your glucose, live",
    description: "This updates in real-time as new readings arrive from your CGM.",
  })}>
    <CurrentBGDisplay />
  </div>

  <div class="flex flex-col-reverse @md:flex-col gap-3 @md:gap-6">
    {#if isMainEnabled(WidgetId.Statistics)}
      <div {@attach coachmark({
        key: "quick-tour.widgets",
        title: "Customizable widgets",
        description: "Reorder or swap these in Settings \u2192 Appearance. You can choose from over a dozen stats.",
      })}>
        <WidgetGrid widgets={topWidgets} maxWidgets={3} />
      </div>
    {/if}

    {#if isMainEnabled(WidgetId.GlucoseChart)}
      <div {@attach coachmark({
        key: "quick-tour.chart",
        title: "Interactive chart",
        description: "Drag to pan, pinch or scroll to zoom. Tap any point to see the exact reading and time.",
      })}>
      <GlucoseChartCard
        showPredictions={isMainEnabled(WidgetId.Predictions) && predictionEnabled}
        defaultFocusHours={focusHours}
        initialChartData={data.initialChartData}
        streamedHistoricalData={data.streamed?.historicalChartData}
      />
      </div>
    {/if}
  </div>

  {#if isMainEnabled(WidgetId.DailyStats)}
    <RecentEntriesCard />
  {/if}

  {#if isMainEnabled(WidgetId.Treatments)}
    <RecentTreatmentsCard />
  {/if}
</div>
