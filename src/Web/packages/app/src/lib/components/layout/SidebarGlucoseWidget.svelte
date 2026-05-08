<script lang="ts">
  import { tryGetRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { STALE_THRESHOLD_MS } from "$lib/constants/staleness";
  import { formatGlucoseValue } from "$lib/utils/formatting";
  import {
    glucoseUnits,
    sidebarWidget,
    haloDialConfig,
  } from "$lib/stores/appearance-store.svelte";
  import { GlucoseValueIndicator } from "$lib/components/shared";
  import HaloDial from "$lib/components/dashboard/halo-dial/HaloDial.svelte";
  import { createChartDataEngine } from "$lib/components/dashboard/glucose-chart/engine/chart-data-engine.svelte";
  import GlucoseChartShell from "$lib/components/dashboard/glucose-chart/GlucoseChartShell.svelte";
  import GlucoseTrack from "$lib/components/dashboard/glucose-chart/tracks/GlucoseTrack.svelte";
  import ThresholdRules from "$lib/components/dashboard/glucose-chart/tracks/ThresholdRules.svelte";

  const realtimeStore = tryGetRealtimeStore();

  // Engine for the sidebar chart — no predictions, no inspection
  // svelte-ignore state_referenced_locally
  const sidebarEngine = createChartDataEngine({
    enablePredictions: false,
    focusHours: 3,
  });

  // Glucose-only layout — no space reserved for basal/IOB/swim lanes
  const sidebarLegend = {
    iob: false,
    cob: false,
    basal: false,
    bolus: false,
    carbs: false,
    deviceEvents: false,
    alarms: false,
    scheduledTrackers: false,
    overrideSpans: false,
    profileSpans: false,
    activitySpans: false,
    pumpModes: false,
    expandedPumpModes: false,
    toggle() {},
  };

  // Collapsed state needs basic BG info
  const rawCurrentBG = $derived(realtimeStore?.currentBG ?? 0);
  const lastUpdated = $derived(realtimeStore?.lastUpdated ?? 0);
  const now = $derived(realtimeStore?.now ?? Date.now());
  const isConnected = $derived(realtimeStore?.isConnected ?? false);
  const isStale = $derived(now - lastUpdated > STALE_THRESHOLD_MS);
  const isDisconnected = $derived(!isConnected);
  const isLoading = $derived(
    rawCurrentBG === 0 && (realtimeStore?.entries.length ?? 0) === 0
  );
  const units = $derived(glucoseUnits.current);
  const displayBG = $derived(formatGlucoseValue(rawCurrentBG, units));
  const widget = $derived(sidebarWidget.current);
</script>

<!-- Expanded state: widget based on preference -->
<div class="group-data-[collapsible=icon]:hidden">
  {#if widget === "halo-dial"}
    <div class="flex justify-center">
      <HaloDial configOverride={haloDialConfig.current} />
    </div>
  {:else}
    <div class="flex flex-col justify-center gap-2">
      <GlucoseValueIndicator
        displayValue={displayBG}
        rawBgMgdl={rawCurrentBG}
        {isLoading}
        {isStale}
        {isDisconnected}
        size="lg"
        class="text-lg"
      />
      <div
        class="px-2 border border-sidebar-border hover:border-sidebar-ring rounded"
      >
        <a href="/">
          <GlucoseChartShell
            engine={sidebarEngine}
            legend={sidebarLegend}
            heightClass="h-[120px]"
            showTimeAxis={false}
            padding={{ left: 0, right: 0, top: 8, bottom: 0 }}
          >
            {#snippet tracks(_ctx)}
              <ThresholdRules />
              <GlucoseTrack showAxis={false} />
            {/snippet}
          </GlucoseChartShell>
        </a>
      </div>
    </div>
  {/if}
</div>

<!-- Collapsed state: just show current BG -->
<div class="hidden group-data-[collapsible=icon]:flex justify-center">
  <GlucoseValueIndicator
    displayValue={displayBG}
    rawBgMgdl={rawCurrentBG}
    {isLoading}
    {isStale}
    {isDisconnected}
    size="xs"
    class="text-lg"
  />
</div>
