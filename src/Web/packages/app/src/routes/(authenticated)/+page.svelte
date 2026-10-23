<script lang="ts">
  import {
    CurrentBGDisplay,
    GlucoseChartCard,
    RecentEntriesCard,
    RecentTreatmentsCard,
    WidgetGrid,
  } from "$lib/components/dashboard";
  import { getUiSettings } from "$api/ui-settings.remote";
  import { dashboardTopWidgets } from "$lib/stores/appearance-store.svelte";
  import { WidgetId } from "$lib/api/generated/nocturne-api-client";
  import { isMainSectionEnabled } from "$lib/types/dashboard-widgets";
  import { coachmark } from "@nocturne/coach";
  import { coachFor } from "$lib/coach-marks/copy.svelte";
  import TenantsOverview from "$lib/components/tenants/TenantsOverview.svelte";
  import HistoryLimitNotice from "$lib/components/layout/HistoryLimitNotice.svelte";
  import FirstReadingChartArea from "$lib/components/dashboard/first-reading/FirstReadingChartArea.svelte";
  import { tryGetRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import type { PageData } from "./$types";

  const { data }: { data: PageData } = $props();

  // svelte-ignore state_referenced_locally
  const uiSettingsQuery = data.tenantless ? null : getUiSettings();
  const featureSettings = $derived(uiSettingsQuery?.current?.features);
  const realtimeStore = tryGetRealtimeStore();

  // Whether we already know this instance has data: server-loaded recent glucose,
  // or anything the realtime feed has surfaced (a live value, or its initial
  // undated most-recent fetch). When it does, the chart renders directly and the
  // first-reading check never runs; only an instance with nothing on hand pays
  // for it.
  const hasInitialGlucose = $derived(
    (data.initialChartData?.glucoseData?.length ?? 0) > 0
  );
  const hasRecentHistory = $derived(
    realtimeStore?.entries.length ? true : false
  );
  const recentHistoryReady = $derived(realtimeStore?.currentReadingLoaded ?? true);
  const hasDataNow = $derived(
    hasInitialGlucose ||
      hasRecentHistory ||
      (realtimeStore ? realtimeStore.currentBG > 0 : false)
  );

  const widgets = $derived(featureSettings?.widgets);

  // Get enabled top widgets from persisted appearance store
  const topWidgets = $derived(dashboardTopWidgets.current);

  // Get focusHours setting for chart default time range
  const focusHours = $derived(
    featureSettings?.display?.focusHours ?? 12
  );

  // Algorithm prediction settings - controls whether predictions are calculated
  const predictionEnabled = $derived(
    uiSettingsQuery?.current?.algorithm?.prediction?.enabled ?? true
  );
</script>

{#if data.tenantless}
  <TenantsOverview />
{:else}
  <div class="@container p-3 @md:p-6 space-y-3 @md:space-y-6">
    <HistoryLimitNotice />

    <div
      {@attach coachmark({
        ...coachFor("quick-tour.current-bg"),
      })}
    >
      <CurrentBGDisplay />
    </div>

    <div class="flex flex-col-reverse @md:flex-col gap-3 @md:gap-6">
      <!--
        Ungated: the grid's contents are a per-user preference
        (`dashboardTopWidgets`), and tenant settings carry no top-placement row
        to gate it with.
      -->
      <div
        {@attach coachmark({
          ...coachFor("quick-tour.widgets"),
        })}
      >
        <WidgetGrid widgets={topWidgets} maxWidgets={3} />
      </div>

      {#if isMainSectionEnabled(widgets, WidgetId.GlucoseChart)}
        {#snippet glucoseChart(chartVisible: boolean)}
          <div
            {@attach chartVisible
              ? coachmark({
                  ...coachFor("quick-tour.chart"),
                })
              : undefined}
          >
            <GlucoseChartCard
              showPredictions={isMainSectionEnabled(
                widgets,
                WidgetId.Predictions
              ) && predictionEnabled}
              defaultFocusHours={focusHours}
              initialChartData={data.initialChartData}
              deferDrawing={data.canViewRealtimeData && !recentHistoryReady}
              initialWindowStart={data.initialWindowStart}
              streamedHistoricalData={data.streamed?.historicalChartData}
            />
          </div>
        {/snippet}

        <FirstReadingChartArea
          chart={glucoseChart}
          bypass={hasDataNow}
          {recentHistoryReady}
          {hasRecentHistory}
        />
      {/if}
    </div>

    {#if isMainSectionEnabled(widgets, WidgetId.DailyStats)}
      <RecentEntriesCard />
    {/if}

    {#if isMainSectionEnabled(widgets, WidgetId.Treatments)}
      <RecentTreatmentsCard />
    {/if}
  </div>
{/if}
