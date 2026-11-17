<script lang="ts">
  import { getRealtimeStore } from "#lib/stores/realtime-store.svelte.js";
  import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
  import {
    formatGlucoseValue,
    formatGlucoseDelta,
  } from "#lib/utils/formatting.js";
  import { getDirectionInfo } from "#lib/utils/index.js";
  import { STALE_THRESHOLD_MS } from "#lib/constants/staleness.js";
  import { displayedGlucose } from "#lib/stores/current-glucose-status.svelte.js";
  import { getGlucoseStatusClass } from "#lib/utils/glucose-status.js";

  const realtimeStore = getRealtimeStore();

  const units = $derived(glucoseUnits.current);
  const glucose = displayedGlucose(realtimeStore);
  const currentBG = $derived(glucose.currentBG);
  const bgDelta = $derived(glucose.bgDelta);
  const lastUpdated = $derived(realtimeStore.lastUpdated);
  const currentTime = $derived(realtimeStore.now);
  const timeSince = $derived(realtimeStore.timeSinceReading);

  const displayBG = $derived(formatGlucoseValue(currentBG, units));
  const displayDelta = $derived(formatGlucoseDelta(bgDelta, units));
  const directionInfo = $derived(getDirectionInfo(glucose.direction));

  const isStale = $derived(currentTime - lastUpdated > STALE_THRESHOLD_MS);
  const isDisconnected = $derived(realtimeStore.connectionUnavailable);
  const isDimmed = $derived(isStale || isDisconnected);
  const hasData = $derived(currentBG > 0);

  const statusClass = $derived(
    getGlucoseStatusClass(glucose.status)
  );

  const statusText = $derived(isDisconnected ? "Connection error" : timeSince);
</script>

<div
  class="flex items-center gap-3 border-b px-3 py-2 text-sm"
  class:opacity-50={isDimmed}
>
  {#if hasData}
    <span class="font-mono font-semibold {statusClass}">
      {displayBG}
    </span>
    <span class="flex items-center gap-1 text-muted-foreground">
      {#if directionInfo.icon}
        {@const Icon = directionInfo.icon}
        <Icon class="h-3.5 w-3.5 {directionInfo.css}" />
      {/if}
      {displayDelta}
    </span>
    <span class="ml-auto text-xs {isDisconnected ? 'text-destructive' : 'text-muted-foreground'}">
      {statusText}
    </span>
  {:else}
    <span class="text-muted-foreground">No data</span>
  {/if}
</div>
