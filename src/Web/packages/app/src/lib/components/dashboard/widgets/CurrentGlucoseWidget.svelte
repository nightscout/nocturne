<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import GlucoseTileWash from "../GlucoseTileWash.svelte";
  import { GlucoseValueIndicator } from "$lib/components/shared";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { createConnectionIndicator } from "$lib/stores/connection-indicator.svelte";
  import { currentGlucoseStatus } from "$lib/stores/current-glucose-status.svelte";
  import { STALE_THRESHOLD_MS } from "$lib/constants/staleness";
  import { getGlucoseTileVariant } from "$lib/utils/glucose-status";
  import { getDirectionInfo } from "$lib/utils";
  import {
    formatGlucoseDelta,
    formatGlucoseValue,
    getUnitLabel,
    minutesAgo,
    time,
  } from "$lib/utils/formatting";
  import { coachmark } from "@nocturne/coach";
  import History from "@lucide/svelte/icons/history";
  import Wifi from "@lucide/svelte/icons/wifi";
  import WifiOff from "@lucide/svelte/icons/wifi-off";

  const realtimeStore = getRealtimeStore();
  const connection = createConnectionIndicator(() => realtimeStore.connectionStatus);

  const units = $derived(glucoseUnits.current);
  const unitLabel = $derived(getUnitLabel(units));
  const mills = $derived(realtimeStore.currentEntry?.mills);
  const tileVariant = $derived(getGlucoseTileVariant(currentGlucoseStatus(mills)));
  const lastUpdated = $derived(realtimeStore.lastUpdated);
  const now = $derived(realtimeStore.now);

  const isLoading = $derived(realtimeStore.currentBG === 0 && realtimeStore.entries.length === 0);
  const isStale = $derived(now - lastUpdated > STALE_THRESHOLD_MS);
  const isDisconnected = $derived(connection.isDisconnected);
  const directionInfo = $derived(getDirectionInfo(realtimeStore.direction));
  const DirectionIcon = $derived(directionInfo.icon);
</script>

{#snippet rangeWash()}
  <GlucoseTileWash {mills} variant={tileVariant} />
{/snippet}

<!-- Unit under the arrow, in the tile's own foreground. The trend is dropped while stale: it
     describes a reading that is no longer current. -->
{#snippet trend()}
  <span class="flex flex-col items-center gap-1">
    {#if !isStale}
      <DirectionIcon
        class="size-5 @[9rem]:size-6 @[12rem]:size-8 @[16rem]:size-10"
        strokeWidth={2.5}
        aria-hidden="true"
      />
      <span class="sr-only">{directionInfo.label}</span>
    {/if}
    <span class="text-xs font-semibold @[9rem]:text-sm">{unitLabel}</span>
  </span>
{/snippet}

<WidgetCard title="Current Glucose">
  <div class="flex flex-col gap-3">
    <!-- The dashboard header hands this mark over while the widget carries the reading;
         see CurrentBGDisplay. -->
    <div
      data-testid="current-glucose-tile"
      {@attach coachmark({
        key: "quick-tour.current-bg",
        title: "Your glucose, live",
        description:
          "This updates in real-time as new readings arrive from your CGM.",
      })}
    >
      <GlucoseValueIndicator
        displayValue={formatGlucoseValue(realtimeStore.currentBG, units)}
        variant={tileVariant}
        {isLoading}
        {isStale}
        {isDisconnected}
        size="xl"
        class="w-full"
        background={rangeWash}
        trailing={isLoading ? undefined : trend}
      />
    </div>

    <div class="flex min-h-5 flex-wrap items-center justify-between gap-x-3 gap-y-1 text-sm">
      <div class="flex items-center gap-3">
        {#if !isLoading && !isStale}
          <p class="flex items-baseline gap-1.5 tabular-nums">
            <span class="font-medium">{formatGlucoseDelta(realtimeStore.bgDelta, units)}</span>
            <span class="text-xs text-muted-foreground">{unitLabel}</span>
          </p>
        {/if}
      </div>

      {#if !isLoading}
        {#if isDisconnected}
          <p class="ml-auto flex items-center gap-1.5 font-medium text-destructive">
            <WifiOff class="size-3.5" aria-hidden="true" />
            Connection Error
          </p>
        {:else}
          <p
            class="ml-auto flex items-center gap-1.5 text-muted-foreground tabular-nums"
            title={time(lastUpdated)}
          >
            {#if isStale}
              <History class="size-3.5" aria-hidden="true" />
            {:else}
              <Wifi class="size-3.5" aria-hidden="true" />
            {/if}
            {minutesAgo(lastUpdated, now)}
          </p>
        {/if}
      {/if}
    </div>
  </div>
</WidgetCard>
