<script lang="ts">
  import { page } from "$app/state";
  import { lazyComponent } from "#lib/utils/lazy-component.svelte.js";
  import { RailItemId, TrackerCategory } from "#lib/api/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import StatusRail from "#lib/components/status-pills/StatusRail.svelte";
  import { effectiveRail } from "#lib/components/status-pills/rail.js";
  import { GlucoseValueIndicator } from "#lib/components/shared/index.js";
  import { getRealtimeStore } from "#lib/stores/realtime-store.svelte.js";
  import {
    dashboardRail,
    dashboardTopWidgets,
    glucoseChartLookback,
    glucoseUnits,
    hiddenLegacyFallbacks,
  } from "#lib/stores/appearance-store.svelte.js";
  import { directionGlyph } from "@nocturne/ui/glucose";
  import StatusPill from "#lib/components/status-pills/StatusPill.svelte";
  import IOBPill from "#lib/components/status-pills/IOBPill.svelte";
  import COBPill from "#lib/components/status-pills/COBPill.svelte";
  import BasalPill from "#lib/components/status-pills/BasalPill.svelte";
  import LoopPill from "#lib/components/status-pills/LoopPill.svelte";
  import TrackerPillBar from "#lib/components/status-pills/TrackerPillBar.svelte";
  import { visibleLegacyFallbacks } from "#lib/components/status-pills/tracker-pill-visibility.js";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import { getGlucoseStatusClass } from "#lib/utils/glucose-status.js";
  import { getUiSettings } from "#lib/api/ui-settings.remote.js";
  import { STALE_THRESHOLD_MS } from "@nocturne/charts/core";
  import {
    formatGlucoseValue,
    formatGlucoseDelta,
    bgLabel,
    formatLocale,
    relativeAge,
    prefersHour12,
  } from "#lib/utils/formatting.js";
  import Clock from "@lucide/svelte/icons/clock";
  import { displayedGlucose } from "#lib/stores/current-glucose-status.svelte.js";
  import { getGlucoseTileVariant } from "#lib/utils/glucose-status.js";
  import { trackUnwashedFill } from "./glucose-tile-wash-state.svelte";
  import { stillArtwork } from "#lib/watercolour-deferral.svelte.js";
  import { showsCurrentGlucoseWidget } from "./top-widget-ids";
  import LogEventButton from "#lib/components/entries/LogEventButton.svelte";

  interface ComponentProps {
    /** Show the status pills besides the tracker pills. */
    showPills?: boolean;
    /**
     * "nightscout" lays the row out as cgm-remote-monitor's header (static/css/main.css
     * .primary): the clock, status pills and Hours row on the left, the reading, its trend,
     * and the major and minor pills on the right, at every width.
     */
    layout?: "default" | "nightscout";
  }

  let { showPills = true, layout = "default" }: ComponentProps = $props();

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

  // The widget carries the reading (and its wash) when it is showing, which frees this row for
  // the pills; otherwise the tile stays here so the reading never leaves the desktop dashboard.
  const readingInWidget = $derived(showsCurrentGlucoseWidget(dashboardTopWidgets.current));

  const glucose = displayedGlucose(realtimeStore);
  const value = $derived(glucose.value);
  const rawBgDelta = $derived(glucose.bgDelta);
  const lastUpdated = $derived(realtimeStore.lastUpdated);
  const tileVariant = $derived(
    getGlucoseTileVariant(glucose.status)
  );

  // Format values based on user's unit preference
  const units = $derived(glucoseUnits.current);
  const displayCurrentBG = $derived(value === null ? "—" : formatGlucoseValue(value, units));
  const displayBgDelta = $derived(value === null ? "" : formatGlucoseDelta(rawBgDelta, units));
  const displayDemoMode = $derived(realtimeStore.demoMode);

  // Current time state (updated every second) from shared store
  const currentTime = $derived(new Date(realtimeStore.now));

  // Stale and connection status
  const isStale = $derived(
    currentTime.getTime() - lastUpdated > STALE_THRESHOLD_MS
  );
  const isDisconnected = $derived(realtimeStore.connectionUnavailable);

  const isLoading = $derived(value === null && !realtimeStore.currentReadingLoaded);
  // Over the flat range fill, so the tile reads the same until it arrives.
  const tileWash = stillArtwork(() => import("./GlucoseTileWash.svelte"));
  trackUnwashedFill(() => ({
    loading: isLoading,
    stale: isStale,
    disconnected: isDisconnected,
    variant: tileVariant,
    washed: tileWash.component !== null,
  }));

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

  const pillRows = $derived(page.data.pillRows);

  // ---- Nightscout layout ----
  /** The focus ranges of Nightscout's "Hours" row. */
  const FOCUS_HOURS = [2, 3, 4, 6, 12, 24, 48] as const;
  const arrow = $derived(directionGlyph(glucose.direction));
  const readingTone = $derived(isStale || isDisconnected ? "" : getGlucoseStatusClass(glucose.status));
  const minutesAgo = $derived(
    lastUpdated > 0 ? Math.max(0, Math.floor((currentTime.getTime() - lastUpdated) / 60000)) : null
  );
  const legacyFallbacks = $derived(
    visibleLegacyFallbacks(realtimeStore.legacyFallbacks, hiddenLegacyFallbacks.current)
  );

  // Tracker Completion Dialog State
  const trackerCompletionDialog = lazyComponent(
    () => import("#lib/components/trackers/TrackerCompletionDialog.svelte"),
  );
  let completingInstanceId = $state<string | null>(null);
  let completingInstanceName = $state("");
  let completingCategory = $state<TrackerCategory | undefined>(undefined);
  let completingDefinitionId = $state<string | undefined>(undefined);

  function handleTrackerComplete(
    instanceId: string,
    instanceName: string,
    category: TrackerCategory,
    definitionId: string
  ) {
    completingInstanceId = instanceId;
    completingInstanceName = instanceName;
    completingCategory = category;
    completingDefinitionId = definitionId;
    trackerCompletionDialog.open = true;
  }

  function handleCompletionDialogClose() {
    trackerCompletionDialog.open = false;
    completingInstanceId = null;
    completingInstanceName = "";
    completingCategory = undefined;
    completingDefinitionId = undefined;
  }
</script>

{#snippet rangeWash()}
  {#if tileWash.component}
    <tileWash.component mills={glucose.mills} variant={tileVariant} delta={rawBgDelta} />
  {/if}
{/snippet}

{#if layout === "nightscout"}
  <h1 class="sr-only">Nocturne</h1>
  <div class="ns-header @container" data-testid="nightscout-status">
    <div class="ns-primary">
      <div class="ns-bg-status">
        <div
          class="ns-bg-button {readingTone}"
          class:ns-stale={isStale}
          title={statusTooltip}
        >
          <span class="ns-current-bg tabular-nums">{isLoading ? "---" : displayCurrentBG}</span>
          <span class="ns-direction" aria-hidden="true">{arrow}</span>
        </div>
        <div class="ns-major-pills">
          <span data-pill-flip class="contents">
            <StatusPill
              value={displayBgDelta}
              label={bgLabel(units)}
              showPopover={false}
            />
          </span>
          {#if railItems.includes(RailItemId.Iob)}
            <IOBPill data={realtimeStore.pillsData.iob} />
          {/if}
        </div>
        <div class="ns-minor-pills">
          {#if railItems.includes(RailItemId.Cob)}
            <COBPill data={realtimeStore.pillsData.cob} />
          {/if}
          {#if railItems.includes(RailItemId.Trackers)}
            <TrackerPillBar
              instances={realtimeStore.trackerInstances}
              definitions={realtimeStore.trackerDefinitions}
              fallbacks={legacyFallbacks}
              now={realtimeStore.now}
              onComplete={handleTrackerComplete}
              class="contents"
            />
          {/if}
          {#if railItems.includes(RailItemId.Basal)}
            <BasalPill data={realtimeStore.pillsData.basal} />
          {/if}
        </div>
      </div>

      <div class="ns-status">
        {#if displayDemoMode}
          <Badge variant="demo">
            <span class="size-2 rounded-full bg-demo animate-pulse" aria-hidden="true"></span>
            Demo mode
          </Badge>
        {/if}
        <div class="ns-clock tabular-nums">{formattedLocalTime}</div>
        <div class="ns-status-pills">
          {#if minutesAgo !== null}
            <span data-pill-flip class="contents">
              <StatusPill
                value={String(minutesAgo)}
                label="min ago"
                level={isStale || isDisconnected ? "urgent" : "none"}
                showPopover={false}
              />
            </span>
          {/if}
          {#if railItems.includes(RailItemId.Reservoir) && realtimeStore.currentReservoir !== null}
            <StatusPill
              value="{realtimeStore.currentReservoir.toFixed(1)}U"
              label="Pump"
              showPopover={false}
            />
          {/if}
          {#if railItems.includes(RailItemId.Loop)}
            <LoopPill data={realtimeStore.pillsData.loop} variant="nightscout" />
          {/if}
        </div>
        <div class="ns-focus-range" data-ns-focus-range>
          <span id="ns-focus-hours">Hours:</span>
          <ToggleGroup.Root
            type="single"
            size="xs"
            spacing={1}
            aria-labelledby="ns-focus-hours"
            bind:value={
              () => String(glucoseChartLookback.current),
              (value: string) => {
                // Clicking the selected range again would clear it; the range stays.
                const hours = Number(value);
                if (FOCUS_HOURS.some((h) => h === hours)) glucoseChartLookback.current = hours;
              }
            }
          >
            {#each FOCUS_HOURS as hours (hours)}
              <ToggleGroup.Item value={String(hours)}>{hours}</ToggleGroup.Item>
            {/each}
          </ToggleGroup.Root>
        </div>
      </div>
    </div>
  </div>
{:else}
<!-- Desktop only: on mobile, MobileHeader carries the reading. -->
<div class="@container">
  <h1 class="sr-only">Nocturne</h1>
  <div class="hidden @md:flex flex-wrap items-center gap-x-6 gap-y-3">
    {#if !readingInWidget}
      <div class="flex shrink-0 items-center gap-3">
        {#if value === null && !isLoading}
          <span class="sr-only">No glucose reading</span>
        {/if}
        <GlucoseValueIndicator
          displayValue={displayCurrentBG}
          variant={tileVariant}
          {isLoading}
          {isStale}
          {isDisconnected}
          {statusText}
          {statusTooltip}
          size="lg"
          background={tileWash.component ? rangeWash : undefined}
        />
        <div class="text-sm text-muted-foreground tabular-nums">
          {displayBgDelta}
        </div>
      </div>
    {/if}

    <StatusRail
      items={railItems}
      rail="desktop"
      initialRows={pillRows?.desktop}
      onTrackerComplete={handleTrackerComplete}
      class="flex-1 @max-xl:order-last @max-xl:basis-full"
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

    <div class="ml-auto flex shrink-0 items-center gap-4">
      <LogEventButton />
      <div class="flex items-center gap-1.5 text-sm text-muted-foreground tabular-nums">
        <Clock class="size-4" aria-hidden="true" />
        {formattedLocalTime}
      </div>
    </div>
  </div>
</div>

<StatusRail
  items={railItems}
  rail="mobile"
  initialRows={pillRows?.mobile}
  onTrackerComplete={handleTrackerComplete}
  hideWhenEmpty
  collapsible
  class="mt-2 @md:hidden"
  testid="status-pills-mobile"
/>
{/if}

{#if trackerCompletionDialog.component}
  <trackerCompletionDialog.component
    bind:open={trackerCompletionDialog.open}
    instanceId={completingInstanceId}
    instanceName={completingInstanceName}
    category={completingCategory}
    definitionId={completingDefinitionId}
    onClose={handleCompletionDialogClose}
  />
{/if}

<style>
  /* Sizes are main.css's, at its default and max-width 1000px / 750px breakpoints. */
  .ns-header {
    color: var(--foreground);
    font-family: "Ubuntu", Verdana, Helvetica, Arial, sans-serif;
  }

  .ns-primary {
    display: flex;
    flex-direction: row-reverse;
    align-items: flex-start;
    justify-content: space-between;
    gap: 1rem;
  }

  .ns-bg-status {
    text-align: center;
    padding-right: 20px;
  }

  .ns-bg-button {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 0.1em;
    margin: 10px;
    min-width: 360px;
    border: 2px solid #000;
    border-radius: 5px;
  }

  .ns-current-bg {
    font-size: 120px;
    line-height: 100px;
  }

  .ns-stale .ns-current-bg {
    text-decoration: line-through;
  }

  .ns-direction {
    font-size: 90px;
    line-height: 90px;
  }

  .ns-major-pills,
  .ns-minor-pills,
  .ns-status-pills {
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: 4px 5px;
  }

  .ns-major-pills {
    font-size: 30px;
    line-height: 34px;
  }

  .ns-minor-pills {
    margin-top: 4px;
    font-size: 22px;
    line-height: 25px;
  }

  .ns-status {
    width: 40%;
    color: var(--ns-status, #808080);
    text-align: center;
  }

  .ns-clock {
    font-size: 100px;
    line-height: 1.15;
    white-space: nowrap;
  }

  .ns-status-pills {
    margin-left: 5px;
    font-size: 20px;
    line-height: 23px;
  }

  /* .focus-range: plain numbers, the selected one in a grey box. */
  .ns-focus-range {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 4px;
    margin: 4px;
    font-size: 18px;
  }

  .ns-focus-range :global([data-ns-focus-range] [data-toggle-group-item]),
  .ns-focus-range :global(button) {
    height: auto;
    min-width: 1.4em;
    padding: 0 2px;
    border: 2px solid transparent;
    border-radius: 5px;
    background: transparent;
    color: inherit;
    font-size: 18px;
  }

  .ns-focus-range :global(button[data-state="on"]) {
    border-color: var(--ns-pill);
    background: var(--ns-pill);
    color: #000;
  }

  .ns-header :global([data-pill]) {
    padding-top: 2px;
    padding-bottom: 2px;
  }

  @container (max-width: 1000px) {
    .ns-current-bg {
      font-size: 80px;
      line-height: 80px;
    }
    .ns-direction {
      font-size: 70px;
      line-height: 70px;
    }
    .ns-bg-button {
      min-width: 0;
    }
    .ns-major-pills {
      font-size: 20px;
      line-height: 24px;
    }
    .ns-minor-pills {
      font-size: 16px;
      line-height: 18px;
    }
    .ns-clock {
      padding-top: 8px;
      font-size: 70px;
    }
    .ns-status-pills {
      font-size: 15px;
      line-height: 17px;
    }
  }

  /* main.css keeps the reading and the clock side by side at this width. */
  @container (max-width: 750px) {
    .ns-primary {
      gap: 0;
    }
    .ns-bg-status {
      width: 50%;
      padding: 0 0 20px 0;
    }
    .ns-bg-button {
      margin: 5px 0;
    }
    .ns-status {
      flex: 1;
      width: auto;
      min-width: 0;
    }
    .ns-current-bg {
      font-size: 70px;
      line-height: 60px;
    }
    .ns-direction {
      font-size: 50px;
      line-height: 50px;
    }
    .ns-major-pills {
      font-size: 15px;
      line-height: 17px;
    }
    .ns-minor-pills,
    .ns-status-pills {
      font-size: 12px;
      line-height: 13px;
    }
    .ns-clock {
      padding-top: 0;
      font-size: 50px;
    }
    .ns-focus-range,
    .ns-focus-range :global(button) {
      font-size: 14px;
    }
  }

  /* Phones: two columns stay, and the 12-hour clock still fits half the width. */
  @container (max-width: 450px) {
    .ns-current-bg {
      font-size: 56px;
      line-height: 56px;
    }
    .ns-direction {
      font-size: 40px;
      line-height: 40px;
    }
    .ns-clock {
      font-size: 34px;
    }
    .ns-focus-range {
      flex-wrap: wrap;
      gap: 0 2px;
    }
  }
</style>
