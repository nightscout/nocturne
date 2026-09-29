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
  import Clock from "@lucide/svelte/icons/clock";
  import { createConnectionIndicator } from "$lib/stores/connection-indicator.svelte";
  import { currentGlucoseStatus } from "$lib/stores/current-glucose-status.svelte";
  import { getGlucoseTileVariant } from "$lib/utils/glucose-status";
  import { Artwork, type PaletteId } from "@nocturne/watercolour";
  import type { GlucoseTileVariant } from "@nocturne/ui/glucose";

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
  const tileVariant = $derived(
    getGlucoseTileVariant(currentGlucoseStatus(realtimeStore.currentEntry?.mills))
  );

  // Presentation only: which paint each server-derived tile variant is washed in.
  // Intensity carries the severity within a hue.
  const washFor: Record<GlucoseTileVariant, { palette: PaletteId; intensity: number } | undefined> = {
    "very-low": { palette: "ember", intensity: 0.9 },
    low: { palette: "ember", intensity: 0.55 },
    "in-range": { palette: "water", intensity: 0.6 },
    high: { palette: "moonlight", intensity: 0.6 },
    "very-high": { palette: "dusk", intensity: 0.9 },
    neutral: undefined,
  };

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

{#snippet rangeWash()}
  <!-- Keyed on the variant: a new reading in the same range leaves the paint alone. The flat fill
       underneath stays, so a wash that cannot draw leaves the plain tile. -->
  {#key tileVariant}
    {@const wash = washFor[tileVariant]}
    {#if wash}
      <Artwork
        artwork="wash"
        palette={wash.palette}
        intensity={wash.intensity}
        durationMs={2000}
        releaseAfterFinish
        fit="fill"
        class="size-full opacity-60 dark:opacity-40"
      />
    {/if}
  {/key}
{/snippet}

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
        background={rangeWash}
      />
      <div class="text-sm text-muted-foreground tabular-nums">
        {displayBgDelta}
      </div>
    </div>

    <div class="flex min-w-0 flex-1 flex-wrap items-center gap-x-2 gap-y-1" data-testid="status-pills">
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
          now={realtimeStore.now}
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
