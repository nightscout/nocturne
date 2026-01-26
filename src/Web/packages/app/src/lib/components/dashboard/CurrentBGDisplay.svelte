<script lang="ts">
  import type { Entry, Treatment } from "$lib/api";
  import { TrackerCategory } from "$lib/api";
  import { Badge } from "$lib/components/ui/badge";
  import {
    COBPill,
    BasalPill,
    IOBPill,
    LoopPill,
    TrackerPillBar,
  } from "$lib/components/status-pills";
  import { GlucoseValueIndicator } from "$lib/components/shared";
  import { TrackerCompletionDialog } from "$lib/components/trackers";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { getSettingsStore } from "$lib/stores/settings-store.svelte";
  import {
    formatGlucoseValue,
    formatGlucoseDelta,
    getUnitLabel,
  } from "$lib/utils/formatting";
  import { Clock } from "lucide-svelte";
  import TreatmentEditDialog from "$lib/components/treatments/TreatmentEditDialog.svelte";
  import { createTreatment } from "../../../routes/reports/treatments/data.remote";

  interface ComponentProps {
    entries?: Entry[];
    currentBG?: number;
    direction?: string;
    bgDelta?: number;
    demoMode?: boolean;
    /**
     * Profile timezone (e.g., "Europe/Stockholm") - if different from local,
     * will show offset
     */
    profileTimezone?: string;
    /** Show status pills (COB, IOB, CAGE, SAGE, etc.) */
    showPills?: boolean;
  }

  let {
    currentBG,
    direction,
    bgDelta,
    demoMode,
    profileTimezone,
    showPills = true,
  }: ComponentProps = $props();

  const realtimeStore = getRealtimeStore();
  const settingsStore = getSettingsStore();

  // Tracker pills global enable setting (visibility is now per-tracker-definition)
  const trackerPillsEnabled = $derived(
    settingsStore.features?.trackerPills?.enabled ?? true
  );

  // Use realtime store values as fallback when props not provided
  const rawCurrentBG = $derived(currentBG ?? realtimeStore.currentBG);
  // Direction is derived but reserved for future use
  void (direction ?? realtimeStore.direction);
  const rawBgDelta = $derived(bgDelta ?? realtimeStore.bgDelta);
  const lastUpdated = $derived(realtimeStore.lastUpdated);

  // Connection status
  const isConnected = $derived(realtimeStore.isConnected);

  // Stale threshold in milliseconds (10 minutes)
  const STALE_THRESHOLD_MS = 10 * 60 * 1000;

  // Format values based on user's unit preference
  const units = $derived(glucoseUnits.current);
  const displayCurrentBG = $derived(formatGlucoseValue(rawCurrentBG, units));
  const displayBgDelta = $derived(formatGlucoseDelta(rawBgDelta, units));
  const unitLabel = $derived(getUnitLabel(units));
  const displayDemoMode = $derived(demoMode ?? realtimeStore.demoMode);

  // Current time state (updated every second) from shared store
  const currentTime = $derived(new Date(realtimeStore.now));

  // Stale and connection status
  const isStale = $derived(
    currentTime.getTime() - lastUpdated > STALE_THRESHOLD_MS
  );
  const isDisconnected = $derived(!isConnected);

  // Loading state - no data received yet
  const isLoading = $derived(
    rawCurrentBG === 0 && realtimeStore.entries.length === 0
  );

  // Time since last reading
  const timeSince = $derived(realtimeStore.timeSinceReading);

  // Status text - show "Connection Error" when disconnected
  const statusText = $derived(isDisconnected ? "Connection Error" : timeSince);

  // Format current time in local timezone
  const formattedLocalTime = $derived(
    currentTime.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
  );

  // Format time in profile timezone if provided and different
  const profileTimeInfo = $derived.by(() => {
    if (!profileTimezone) return null;

    try {
      const localTz = Intl.DateTimeFormat().resolvedOptions().timeZone;
      if (localTz === profileTimezone) return null;

      const profileTime = currentTime.toLocaleTimeString([], {
        hour: "2-digit",
        minute: "2-digit",
        timeZone: profileTimezone,
      });

      // Calculate offset between timezones
      const localDate = new Date(
        currentTime.toLocaleString("en-US", { timeZone: localTz })
      );
      const profileDate = new Date(
        currentTime.toLocaleString("en-US", { timeZone: profileTimezone })
      );
      const diffHours = Math.round(
        (profileDate.getTime() - localDate.getTime()) / (1000 * 60 * 60)
      );
      const offsetStr = diffHours >= 0 ? `+${diffHours}h` : `${diffHours}h`;

      return {
        time: profileTime,
        timezone:
          profileTimezone.split("/").pop()?.replace(/_/g, " ") ??
          profileTimezone,
        offset: offsetStr,
      };
    } catch {
      return null;
    }
  });

  // Treatment Dialog State
  let showTreatmentDialog = $state(false);
  let treatmentEventType = $state("");
  let isSavingTreatment = $state(false);

  function handleAddTreatment(
    type: "Site Change" | "Sensor Change" | "Sensor Start"
  ) {
    treatmentEventType = type;
    showTreatmentDialog = true;
  }

  async function handleSaveTreatment(treatment: Treatment) {
    isSavingTreatment = true;
    try {
      await createTreatment({ treatmentData: treatment });
      showTreatmentDialog = false;
    } catch (e) {
      console.error("Failed to create treatment", e);
      // TODO: Toast error
    } finally {
      isSavingTreatment = false;
    }
  }

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

<!-- Header section - hidden on mobile since MobileHeader handles BG display -->
<div class="@container">
  <h1 class="sr-only">Nocturne</h1>
  <div class="hidden @md:flex items-center justify-between gap-6">
    <!-- Left side: Demo badge + COB/Basal pills + Tracker Pills -->
    <div class="flex items-center gap-2 order-1">
      {#if displayDemoMode}
        <Badge variant="secondary" class="flex items-center gap-1">
          <div class="w-2 h-2 bg-blue-500 rounded-full animate-pulse"></div>
          Demo Mode
        </Badge>
      {/if}
      <!-- All status pills in a single line -->
      {#if showPills}
        <COBPill data={realtimeStore.pillsData.cob} />
        <BasalPill data={realtimeStore.pillsData.basal} />
        <IOBPill data={realtimeStore.pillsData.iob} />
        <LoopPill data={realtimeStore.pillsData.loop} />
      {/if}
      <!-- Tracker Pills -->
      {#if trackerPillsEnabled && realtimeStore.trackerInstances.length > 0}
        <TrackerPillBar
          instances={realtimeStore.trackerInstances}
          definitions={realtimeStore.trackerDefinitions}
          onComplete={handleTrackerComplete}
          class="flex-nowrap"
        />
      {/if}
    </div>

    <!-- Right side: Clock, BG, Delta (semantic order: BG first for accessibility) -->
    <div class="flex items-center gap-4 order-2">
      <!-- BG Display with connection/stale status - semantically first -->
      <div class="flex items-center gap-2 order-2">
        <GlucoseValueIndicator
          displayValue={displayCurrentBG}
          rawBgMgdl={rawCurrentBG}
          {isLoading}
          {isStale}
          {isDisconnected}
          {statusText}
          statusTooltip="Last reading: {timeSince}"
          size="lg"
        />
        <div class="text-center">
          <div class="text-2xl">
            <!-- Direction display placeholder -->
          </div>
          <div class="text-sm text-muted-foreground">
            {displayBgDelta}
          </div>
        </div>
      </div>
      <!-- Current Time Display - visually first -->
      <div
        class="flex items-center gap-2 text-lg font-medium tabular-nums order-1"
      >
        <Clock class="h-4 w-4 text-muted-foreground" />
        {formattedLocalTime}
        {#if profileTimeInfo}
          <div
            class="text-xs text-muted-foreground flex items-center gap-1 ml-1"
          >
            <span class="font-medium">{profileTimeInfo.timezone}:</span>
            <span class="tabular-nums">{profileTimeInfo.time}</span>
            <Badge variant="outline" class="text-[10px] px-1 py-0">
              {profileTimeInfo.offset}
            </Badge>
          </div>
        {/if}
      </div>
    </div>
  </div>
</div>

<!-- Status Pills Bar - visible only on mobile (all pills in header on desktop) -->
{#if showPills}
  <div class="mt-2 flex flex-wrap items-center gap-2 @md:hidden">
    <COBPill data={realtimeStore.pillsData.cob} />
    <BasalPill data={realtimeStore.pillsData.basal} />
    <IOBPill data={realtimeStore.pillsData.iob} />
    <LoopPill data={realtimeStore.pillsData.loop} />
  </div>
{/if}

<TreatmentEditDialog
  bind:open={showTreatmentDialog}
  treatment={null}
  availableEventTypes={[treatmentEventType]}
  isLoading={isSavingTreatment}
  onClose={() => (showTreatmentDialog = false)}
  onSave={handleSaveTreatment}
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
