<script lang="ts">
  import { tryGetRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { STALE_THRESHOLD_MS } from "$lib/constants/staleness";
  import {
    formatGlucoseValue,
    formatGlucoseDelta,
  } from "$lib/utils/formatting";
  import { deltaColorClass, trendAngle } from "@nocturne/ui/glucose";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { GlucoseValueIndicator } from "$lib/components/shared";
  import { createChartDataEngine } from "$lib/components/dashboard/glucose-chart/engine/chart-data-engine.svelte";
  import GlucoseChartShell from "$lib/components/dashboard/glucose-chart/GlucoseChartShell.svelte";
  import GlucoseTrack from "$lib/components/dashboard/glucose-chart/tracks/GlucoseTrack.svelte";
  import ThresholdRules from "$lib/components/dashboard/glucose-chart/tracks/ThresholdRules.svelte";
  import { Tween, prefersReducedMotion } from "svelte/motion";
  import { cubicOut } from "svelte/easing";
  import ArrowRight from "lucide-svelte/icons/arrow-right";

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
    basalInjections: false,
    overrideSpans: false,
    profileSpans: false,
    activitySpans: false,
    pumpModes: false,
    expandedPumpModes: false,
    toggle(_key: string) {},
  };

  // Collapsed state needs basic BG info
  const rawCurrentBG = $derived(realtimeStore?.currentBG ?? 0);
  const lastUpdated = $derived(realtimeStore?.lastUpdated ?? 0);
  const now = $derived(realtimeStore?.now ?? Date.now());
  const isConnected = $derived(realtimeStore?.isConnected ?? false);
  const isStale = $derived(now - lastUpdated > STALE_THRESHOLD_MS);

  // Debounce the connected→disconnected transition so a brief blip (e.g. the
  // socket reconnecting during page load) doesn't flash the "Connection error"
  // state. Reconnecting clears it immediately; dropping waits this long first.
  const DISCONNECT_GRACE_MS = 3000;
  let isDisconnected = $state(false);
  $effect(() => {
    if (isConnected) {
      isDisconnected = false;
      return;
    }
    const timeout = setTimeout(() => {
      isDisconnected = true;
    }, DISCONNECT_GRACE_MS);
    return () => clearTimeout(timeout);
  });
  const isLoading = $derived(
    rawCurrentBG === 0 && (realtimeStore?.entries.length ?? 0) === 0
  );
  const units = $derived(glucoseUnits.current);
  const displayBG = $derived(formatGlucoseValue(rawCurrentBG, units));

  // Trend metadata
  const bgDelta = $derived(realtimeStore?.bgDelta ?? 0);
  const direction = $derived(realtimeStore?.direction ?? "");
  const timeSinceReading = $derived(realtimeStore?.timeSinceReading ?? "");
  const displayDelta = $derived(formatGlucoseDelta(bgDelta, units));
  const hasData = $derived(!isLoading && rawCurrentBG > 0);


  // Smoothly animate the trend arrow rotation
  const arrowAngle = Tween.of(() => trendAngle(bgDelta), {
    // Reactive, so toggling the OS setting takes effect without a reload.
    duration: prefersReducedMotion.current ? 0 : 600,
    easing: cubicOut,
  });
</script>

<!-- Expanded state -->
<div class="group-data-[collapsible=icon]:hidden">
  <div class="flex flex-col justify-center gap-2">
    <div class="flex items-center justify-center gap-2">
      <GlucoseValueIndicator
        displayValue={displayBG}
        rawBgMgdl={rawCurrentBG}
        {isLoading}
        {isStale}
        {isDisconnected}
        size="lg"
        class="text-lg"
      />
      {#if hasData && !isStale}
        <div class="flex flex-col items-center gap-0.5">
          <div class="flex items-center gap-0.5 {deltaColorClass(direction)}">
            <ArrowRight
              class="size-4"
              style="transform: rotate({arrowAngle.current}deg)"
            />
            <span class="text-sm font-medium">{displayDelta}</span>
          </div>
          <span class="text-[10px] text-muted-foreground leading-tight">
            {timeSinceReading}
          </span>
        </div>
      {/if}
    </div>
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
</div>

<!-- Collapsed state: BG + small arrow + delta -->
<div class="hidden group-data-[collapsible=icon]:flex flex-col items-center gap-0.5">
  <GlucoseValueIndicator
    displayValue={displayBG}
    rawBgMgdl={rawCurrentBG}
    {isLoading}
    {isStale}
    {isDisconnected}
    size="xs"
    class="text-lg"
  />
  {#if hasData && !isStale}
    <div class="flex items-center gap-0.5 {deltaColorClass(direction)}">
      <ArrowRight
        class="size-3"
        style="transform: rotate({arrowAngle.current}deg)"
      />
      <span class="text-[10px] font-medium">{displayDelta}</span>
    </div>
  {/if}
</div>
