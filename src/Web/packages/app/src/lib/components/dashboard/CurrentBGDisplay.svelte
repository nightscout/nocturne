<script lang="ts">
  import { page } from "$app/state";
  import { lazyComponent } from "$lib/utils/lazy-component.svelte";
  import { RailItemId, TrackerCategory } from "$lib/api";
  import { Badge } from "$lib/components/ui/badge";
  import StatusRail from "$lib/components/status-pills/StatusRail.svelte";
  import { effectiveRail } from "$lib/components/status-pills/rail";
  import { GlucoseValueIndicator } from "$lib/components/shared";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { dashboardRail, glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { getUiSettings } from "$api/ui-settings.remote";
  import { STALE_THRESHOLD_MS } from "$lib/constants/staleness";
  import {
    formatGlucoseValue,
    formatGlucoseDelta,
    formatLocale,
    relativeAge,
    prefersHour12,
  } from "$lib/utils/formatting";
  import Clock from "@lucide/svelte/icons/clock";
  import { displayedGlucose } from "$lib/stores/current-glucose-status.svelte";
  import { getGlucoseTileVariant } from "$lib/utils/glucose-status";

  interface ComponentProps {
    /** Show the status pills besides the tracker pills. */
    showPills?: boolean;
  }

  let { showPills = true }: ComponentProps = $props();

  const realtimeStore = getRealtimeStore();
  const uiSettingsQuery = getUiSettings();

  // Trackers show only while the tenant's tracker pill switch is on.
  const trackerPillsEnabled = $derived(
    uiSettingsQuery.current?.features?.trackerPills?.enabled ?? true
  );
  const rail = $derived(effectiveRail(dashboardRail.current, trackerPillsEnabled));
  const railItems = $derived(
    showPills ? rail : rail.filter((item) => item === RailItemId.Trackers)
  );

  const glucose = displayedGlucose(realtimeStore);
  const rawCurrentBG = $derived(glucose.currentBG);
  const rawBgDelta = $derived(glucose.bgDelta);
  const lastUpdated = $derived(realtimeStore.lastUpdated);
  const tileVariant = $derived(
    getGlucoseTileVariant(glucose.status)
  );

  // Format values based on user's unit preference
  const units = $derived(glucoseUnits.current);
  const displayCurrentBG = $derived(formatGlucoseValue(rawCurrentBG, units));
  const displayBgDelta = $derived(formatGlucoseDelta(rawBgDelta, units));
  const displayDemoMode = $derived(realtimeStore.demoMode);

  // Current time state (updated every second) from shared store
  const currentTime = $derived(new Date(realtimeStore.now));

  // Stale and connection status
  const isStale = $derived(
    currentTime.getTime() - lastUpdated > STALE_THRESHOLD_MS
  );
  const isDisconnected = $derived(realtimeStore.connectionUnavailable);

  // Loading state - no data received yet
  const isLoading = $derived(
    rawCurrentBG === 0 && realtimeStore.entries.length === 0
  );

  function formatTimeSinceLastReading(): string {
    return relativeAge(lastUpdated, {
      now: currentTime.getTime(),
      fallback: "Unknown",
    });
  }

  // Status text - show "Connection error" when disconnected
  const statusText = $derived.by(() =>
    isDisconnected ? "Connection error" : formatTimeSinceLastReading()
  );

  const statusTooltip = $derived.by(
    () => `Last reading: ${formatTimeSinceLastReading()}`
  );

  const formattedLocalTime = $derived(
    currentTime.toLocaleTimeString(formatLocale(), {
      hour: "2-digit",
      minute: "2-digit",
      hour12: prefersHour12(),
    })
  );

  // The desktop key predates the mobile rail, so rows stored before it still apply.
  const tenantSlug = $derived(page.data.tenantSlug ?? "");
  const desktopRowsKey = $derived(`nocturne:status-pill-rows:${tenantSlug}`);
  const mobileRowsKey = $derived(`nocturne:status-pill-rows:mobile:${tenantSlug}`);

  // Tracker Completion Dialog State
  const trackerCompletionDialog = lazyComponent(
    () => import("$lib/components/trackers/TrackerCompletionDialog.svelte"),
  );
  let completingInstanceId = $state<string | null>(null);
  let completingInstanceName = $state("");
  let completingCategory = $state<TrackerCategory | undefined>(undefined);
  let completingDefinitionId = $state<string | undefined>(undefined);
  let completingCompletionEventType = $state<string | undefined>(undefined);

  function handleTrackerComplete(
    instanceId: string,
    instanceName: string,
    category: TrackerCategory,
    definitionId: string,
    completionEventType?: string
  ) {
    completingInstanceId = instanceId;
    completingInstanceName = instanceName;
    completingCategory = category;
    completingDefinitionId = definitionId;
    completingCompletionEventType = completionEventType;
    trackerCompletionDialog.open = true;
  }

  function handleCompletionDialogClose() {
    trackerCompletionDialog.open = false;
    completingInstanceId = null;
    completingInstanceName = "";
    completingCategory = undefined;
    completingDefinitionId = undefined;
    completingCompletionEventType = undefined;
  }
</script>

<!-- Desktop only: on mobile, MobileHeader carries the reading. -->
<div class="@container">
  <h1 class="sr-only">Nocturne</h1>
  <div class="hidden @md:flex items-center gap-6">
    <div class="flex shrink-0 items-center gap-3">
      <GlucoseValueIndicator
        displayValue={displayCurrentBG}
        variant={tileVariant}
        {isLoading}
        {isStale}
        {isDisconnected}
        {statusText}
        {statusTooltip}
        size="lg"
      />
      <div class="text-sm text-muted-foreground tabular-nums">
        {displayBgDelta}
      </div>
    </div>

    <StatusRail
      items={railItems}
      rowsKey={desktopRowsKey}
      onTrackerComplete={handleTrackerComplete}
      class="flex-1"
      testid="status-pills"
    >
      {#snippet leading()}
        {#if displayDemoMode}
          <Badge variant="demo">
            <span class="size-2 rounded-full bg-demo animate-pulse" aria-hidden="true"></span>
            Demo mode
          </Badge>
        {/if}
      {/snippet}
    </StatusRail>

    <div class="flex shrink-0 items-center gap-1.5 text-sm text-muted-foreground tabular-nums">
      <Clock class="size-4" aria-hidden="true" />
      {formattedLocalTime}
    </div>
  </div>
</div>

<StatusRail
  items={railItems}
  rowsKey={mobileRowsKey}
  onTrackerComplete={handleTrackerComplete}
  hideWhenEmpty
  class="mt-2 @md:hidden"
  testid="status-pills-mobile"
/>

{#if trackerCompletionDialog.component}
  <trackerCompletionDialog.component
    bind:open={trackerCompletionDialog.open}
    instanceId={completingInstanceId}
    instanceName={completingInstanceName}
    category={completingCategory}
    definitionId={completingDefinitionId}
    completionEventType={completingCompletionEventType}
    onClose={handleCompletionDialogClose}
  />
{/if}
