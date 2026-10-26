<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import { trackUnwashedFill } from "../glucose-tile-wash-state.svelte";
  import { stillArtwork } from "#lib/watercolour-deferral.svelte.js";
  import { GlucoseValueIndicator } from "#lib/components/shared/index.js";
  import { getRealtimeStore } from "#lib/stores/realtime-store.svelte.js";
  import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
  import { displayedGlucose } from "#lib/stores/current-glucose-status.svelte.js";
  import { STALE_THRESHOLD_MS } from "#lib/constants/staleness.js";
  import { getGlucoseTileVariant } from "#lib/utils/glucose-status.js";
  import { getDirectionInfo } from "#lib/utils/index.js";
  import {
    formatGlucoseDelta,
    formatGlucoseValue,
    getUnitLabel,
    relativeAge,
    time,
  } from "#lib/utils/formatting.js";
  import { getCurrentBatteryStatus } from "#lib/api/generated/batteries.generated.remote.js";
  import { coachmark } from "@nocturne/coach";
  import { coachFor } from "#lib/coach-marks/copy.svelte.js";
  import History from "@lucide/svelte/icons/history";
  import BatteryCharging from "@lucide/svelte/icons/battery-charging";
  import BatteryFull from "@lucide/svelte/icons/battery-full";
  import BatteryLow from "@lucide/svelte/icons/battery-low";
  import BatteryMedium from "@lucide/svelte/icons/battery-medium";
  import BatteryWarning from "@lucide/svelte/icons/battery-warning";
  import Wifi from "@lucide/svelte/icons/wifi";
  import WifiOff from "@lucide/svelte/icons/wifi-off";

  const realtimeStore = getRealtimeStore();

  const units = $derived(glucoseUnits.current);
  const unitLabel = $derived(getUnitLabel(units));
  const glucose = displayedGlucose(realtimeStore);
  const tileVariant = $derived(getGlucoseTileVariant(glucose.status));
  const lastUpdated = $derived(realtimeStore.lastUpdated);
  const now = $derived(realtimeStore.now);

  const isLoading = $derived(glucose.currentBG === 0 && realtimeStore.entries.length === 0);
  const isStale = $derived(now - lastUpdated > STALE_THRESHOLD_MS);
  const isDisconnected = $derived(realtimeStore.connectionUnavailable);
  const directionInfo = $derived(getDirectionInfo(glucose.direction));
  const DirectionIcon = $derived(directionInfo.icon);
  // Over the flat range fill, so the tile reads the same until it arrives.
  const tileWash = stillArtwork(() => import("../GlucoseTileWash.svelte"));
  trackUnwashedFill(() => ({ loading: isLoading, stale: isStale, disconnected: isDisconnected, variant: tileVariant, washed: tileWash.component !== null }));

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
  {#if tileWash.component}
    <tileWash.component mills={glucose.mills} variant={tileVariant} delta={glucose.bgDelta} />
  {/if}
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
        {relativeAge(lastUpdated, { now, fallback: "Unknown" })}
      </p>
    {/if}
  {/if}
{/snippet}

<!-- Held empty while the reading loads, as the placeholder holds it, since a fresh reading fills it;
     dropped when empty (a stale reading with no battery report), so no blank gap sits under the
     tile. -->
{#snippet details(battery: Awaited<typeof batteryStatusPromise> | undefined)}
  {@const uploader = battery?.min && Object.keys(battery.devices ?? {}).length > 0 ? battery : undefined}
  {#if isLoading || !isStale || uploader}
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
          {uploader.min?.battery != null ? `${uploader.min.battery}%` : uploader.display}
        </span>
      {/if}
    </div>
  {/if}
{/snippet}

<WidgetCard title="Current glucose" subtitleSnippet={lastReading}>
  <div class="flex flex-col gap-3">
    <!-- The dashboard header hands this mark over while the widget carries the reading;
         see routes/(authenticated)/+page.svelte. -->
    <div
      data-testid="current-glucose-tile"
      {@attach coachmark({
        ...coachFor("quick-tour.current-bg"),
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
        background={tileWash.component ? rangeWash : undefined}
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
