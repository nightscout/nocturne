<script lang="ts">
  import { TrackerCategory } from "$lib/api";
  import { Badge } from "$lib/components/ui/badge";
  import {
    COBPill,
    BasalPill,
    IOBPill,
    LoopPill,
    ReservoirPill,
    TrackerPillBar,
  } from "$lib/components/status-pills";
  import { GlucoseValueIndicator } from "$lib/components/shared";
  import { TrackerCompletionDialog } from "$lib/components/trackers";
  import { EntryEditDialog } from "$lib/components/entries";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { getSettingsStore } from "$lib/stores/settings-store.svelte";
  import { STALE_THRESHOLD_MS } from "$lib/constants/staleness";
  import {
    formatGlucoseValue,
    formatGlucoseDelta,
    formatLocale,
    minutesAgo,
    prefersHour12,
  } from "$lib/utils/formatting";
  import { Clock } from "lucide-svelte";
  import { createConnectionIndicator } from "$lib/stores/connection-indicator.svelte";

  interface ComponentProps {
    /** Show status pills (COB, IOB, CAGE, SAGE, etc.) */
    showPills?: boolean;
  }

  let { showPills = true }: ComponentProps = $props();

  const realtimeStore = getRealtimeStore();
  const settingsStore = getSettingsStore();

  // Tracker pills global enable setting (visibility is now per-tracker-definition)
  const trackerPillsEnabled = $derived(
    settingsStore.features?.trackerPills?.enabled ?? true
  );

  const rawCurrentBG = $derived(realtimeStore.currentBG);
  const rawBgDelta = $derived(realtimeStore.bgDelta);
  const lastUpdated = $derived(realtimeStore.lastUpdated);

  const connection = createConnectionIndicator(() => realtimeStore.connectionStatus);


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
  const isDisconnected = $derived(connection.isDisconnected);

  // Loading state - no data received yet
  const isLoading = $derived(
    rawCurrentBG === 0 && realtimeStore.entries.length === 0
  );

  function formatTimeSinceLastReading(): string {
    return minutesAgo(lastUpdated, currentTime.getTime());
  }

  // Status text - show "Connection Error" when disconnected
  const statusText = $derived.by(() =>
    isDisconnected ? "Connection Error" : formatTimeSinceLastReading()
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

  // Entry Dialog State
  let showEntryDialog = $state(false);

  // Tracker Completion Dialog State
  let showCompletionDialog = $state(false);
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
    showCompletionDialog = true;
  }

  function handleCompletionDialogClose() {
    showCompletionDialog = false;
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
        rawBgMgdl={rawCurrentBG}
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

    <div class="flex min-w-0 flex-1 flex-wrap items-center gap-x-2 gap-y-1">
      {#if displayDemoMode}
        <Badge variant="demo">
          <span class="size-2 rounded-full bg-demo animate-pulse" aria-hidden="true"></span>
          Demo Mode
        </Badge>
      {/if}
      {#if showPills}
        <COBPill data={realtimeStore.pillsData.cob} />
        <BasalPill data={realtimeStore.pillsData.basal} />
        <IOBPill data={realtimeStore.pillsData.iob} />
        <LoopPill data={realtimeStore.pillsData.loop} />
        <!-- Many pumps and pods report no numeric reservoir (e.g. Omnipod above 50 U). -->
        {#if realtimeStore.currentReservoir !== null}
          <ReservoirPill reservoir={realtimeStore.currentReservoir} />
        {/if}
      {/if}
      {#if trackerPillsEnabled && realtimeStore.trackerInstances.length > 0}
        <TrackerPillBar
          instances={realtimeStore.trackerInstances}
          definitions={realtimeStore.trackerDefinitions}
          onComplete={handleTrackerComplete}
          class="contents"
        />
      {/if}
    </div>

    <div class="flex shrink-0 items-center gap-1.5 text-sm text-muted-foreground tabular-nums">
      <Clock class="size-4" aria-hidden="true" />
      {formattedLocalTime}
    </div>
  </div>
</div>

{#if showPills}
  <div class="mt-2 flex flex-wrap items-center gap-x-2 gap-y-1 @md:hidden">
    <COBPill data={realtimeStore.pillsData.cob} />
    <BasalPill data={realtimeStore.pillsData.basal} />
    <IOBPill data={realtimeStore.pillsData.iob} />
    <LoopPill data={realtimeStore.pillsData.loop} />
  </div>
{/if}

<EntryEditDialog
  bind:open={showEntryDialog}
  entry={null}
  onClose={() => (showEntryDialog = false)}
/>

<TrackerCompletionDialog
  bind:open={showCompletionDialog}
  instanceId={completingInstanceId}
  instanceName={completingInstanceName}
  category={completingCategory}
  definitionId={completingDefinitionId}
  completionEventType={completingCompletionEventType}
  onClose={handleCompletionDialogClose}
/>
