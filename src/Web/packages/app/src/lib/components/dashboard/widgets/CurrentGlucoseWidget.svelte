<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import GlucoseTileWash, { trackUnwashedFill } from "../GlucoseTileWash.svelte";
  import { GlucoseValueIndicator } from "$lib/components/shared";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { createConnectionIndicator } from "$lib/stores/connection-indicator.svelte";
  import { displayedGlucose } from "$lib/stores/current-glucose-status.svelte";
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
  import { getCurrentBatteryStatus } from "$api/generated/batteries.generated.remote";
  import { coachmark } from "@nocturne/coach";
  import History from "@lucide/svelte/icons/history";
  import BatteryCharging from "@lucide/svelte/icons/battery-charging";
  import BatteryFull from "@lucide/svelte/icons/battery-full";
  import BatteryLow from "@lucide/svelte/icons/battery-low";
  import BatteryMedium from "@lucide/svelte/icons/battery-medium";
  import BatteryWarning from "@lucide/svelte/icons/battery-warning";
  import Wifi from "@lucide/svelte/icons/wifi";
  import WifiOff from "@lucide/svelte/icons/wifi-off";

  const realtimeStore = getRealtimeStore();
  const connection = createConnectionIndicator(() => realtimeStore.connectionStatus);

  const units = $derived(glucoseUnits.current);
  const unitLabel = $derived(getUnitLabel(units));
  const glucose = displayedGlucose(realtimeStore);
  const tileVariant = $derived(getGlucoseTileVariant(glucose.status));
  const lastUpdated = $derived(realtimeStore.lastUpdated);
  const now = $derived(realtimeStore.now);

  const isLoading = $derived(glucose.currentBG === 0 && realtimeStore.entries.length === 0);
  const isStale = $derived(now - lastUpdated > STALE_THRESHOLD_MS);
  const isDisconnected = $derived(connection.isDisconnected);
  const directionInfo = $derived(getDirectionInfo(glucose.direction));
  const DirectionIcon = $derived(directionInfo.icon);
  trackUnwashedFill(() => ({ loading: isLoading, stale: isStale, disconnected: isDisconnected, variant: tileVariant }));

  const batteryStatusPromise = getCurrentBatteryStatus({ recentMinutes: 30 });

  function batteryIcon(level: number | undefined) {
    if (!level) return BatteryWarning;
    if (level >= 95) return BatteryFull;
    if (level >= 50) return BatteryMedium;
    if (level >= 25) return BatteryLow;
    return BatteryWarning;
  }
</script>

{#snippet rangeWash()}
  <GlucoseTileWash mills={glucose.mills} variant={tileVariant} delta={glucose.bgDelta} />
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

{#snippet lastReading()}
  {#if !isLoading}
    {#if isDisconnected}
      <p class="flex items-center gap-1.5 text-sm font-medium whitespace-nowrap text-destructive">
        <WifiOff class="size-3.5" aria-hidden="true" />
        Connection Error
      </p>
    {:else}
      <p
        class="flex items-center gap-1.5 text-sm whitespace-nowrap text-muted-foreground tabular-nums"
        title={time(lastUpdated)}
      >
        {#if isStale}
          <History class="size-3.5" aria-hidden="true" />
          <span class="sr-only">Stale reading</span>
        {:else}
          <Wifi class="size-3.5" aria-hidden="true" />
        {/if}
        {minutesAgo(lastUpdated, now)}
      </p>
    {/if}
  {/if}
{/snippet}

<!-- Dropped when empty (a stale reading with no battery report), so no blank gap sits under the
     tile. -->
{#snippet details(battery: Awaited<typeof batteryStatusPromise> | undefined)}
  {@const uploader = battery?.min && Object.keys(battery.devices ?? {}).length > 0 ? battery : undefined}
  {#if (!isLoading && !isStale) || uploader}
    <div class="flex min-h-5 flex-wrap items-center gap-x-3 gap-y-1 text-sm">
      {#if !isLoading && !isStale}
        <p class="flex items-baseline gap-1.5 tabular-nums">
          <span class="font-medium">{formatGlucoseDelta(glucose.bgDelta, units)}</span>
          <span class="text-xs text-muted-foreground">{unitLabel}</span>
        </p>
      {/if}

      {#if uploader}
        {@const BatteryIcon = batteryIcon(uploader.level)}
        <span
          class="inline-flex items-center gap-1 rounded-full px-1.5 py-0.5 text-xs font-medium {uploader.status ===
          'urgent'
            ? 'bg-destructive/20 text-destructive'
            : uploader.status === 'warn'
              ? 'bg-warning/20 text-warning'
              : 'bg-success/20 text-success'}"
        >
          {#if uploader.min?.isCharging}
            <BatteryCharging class="size-3" aria-hidden="true" />
          {:else}
            <BatteryIcon class="size-3" aria-hidden="true" />
          {/if}
          <span class="sr-only">Uploader battery</span>
          {uploader.display}
        </span>
      {/if}
    </div>
  {/if}
{/snippet}

<WidgetCard title="Current Glucose" subtitleSnippet={lastReading}>
  <div class="flex flex-col gap-3">
    <!-- The dashboard header hands this mark over while the widget carries the reading;
         see routes/(authenticated)/+page.svelte. -->
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
        displayValue={formatGlucoseValue(glucose.currentBG, units)}
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

    {#await batteryStatusPromise}
      {@render details(undefined)}
    {:then currentStatus}
      {@render details(currentStatus)}
    {:catch}
      <!-- The battery chip is optional; the reading does not wait on it. -->
      {@render details(undefined)}
    {/await}
  </div>
</WidgetCard>
