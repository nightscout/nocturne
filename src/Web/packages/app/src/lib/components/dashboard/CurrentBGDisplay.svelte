<script lang="ts">
  import { browser } from "$app/environment";
  import { page } from "$app/state";
  import { lazyComponent } from "$lib/utils/lazy-component.svelte";
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
  import { displayedGlucose } from "$lib/stores/current-glucose-status.svelte";
  import { getGlucoseTileVariant } from "$lib/utils/glucose-status";

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

  // The pills arrive after the page paints and can wrap onto more rows than the one the row
  // starts with, so the rows last rendered for this tenant are held until they do. Until then
  // the pills are empty placeholders, whose row count says nothing about the loaded ones.
  // StatusPill is 2.75rem tall (text-xs label, text-sm value, py-1) and rows sit gap-y-1 apart.
  const pillRowsKey = $derived(`nocturne:status-pill-rows:${page.data.tenantSlug ?? ""}`);

  const MAX_PILL_ROWS = 4;

  function storedPillRows(): number {
    try {
      const rows = Number(localStorage.getItem(pillRowsKey));
      return Number.isInteger(rows) && rows > 1 ? Math.min(rows, MAX_PILL_ROWS) : 1;
    } catch {
      return 1;
    }
  }

  let reservedPillRows = $state(browser ? storedPillRows() : 1);
  let pillsEl = $state<HTMLElement>();

  function renderedPillRows(el: HTMLElement): number {
    const items = [...el.children].flatMap((child) =>
      getComputedStyle(child).display === "contents" ? [...child.children] : [child]
    );
    const centres = items
      .map((item) => item.getBoundingClientRect())
      .filter((rect) => rect.height > 0)
      .map((rect) => Math.round(rect.top + rect.height / 2));
    return new Set(centres).size;
  }

  $effect(() => {
    const el = pillsEl;
    if (!el || !realtimeStore.isReady) return;
    const observer = new ResizeObserver(() => {
      const rows = renderedPillRows(el);
      if (rows === 0) return;
      reservedPillRows = rows;
      try {
        localStorage.setItem(pillRowsKey, String(rows));
      } catch {
        // Storage is unavailable; the next load reserves one row.
      }
    });
    observer.observe(el);
    return () => observer.disconnect();
  });

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

    <div
      class="min-h-[calc(var(--pill-rows)*2.75rem+(var(--pill-rows)-1)*0.25rem)] min-w-0 flex-1"
      style:--pill-rows={reservedPillRows}
      data-testid="status-pills"
    >
      <div bind:this={pillsEl} class="flex flex-wrap items-center gap-x-2 gap-y-1">
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
