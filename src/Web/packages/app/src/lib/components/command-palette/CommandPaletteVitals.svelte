<script lang="ts">
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import {
    formatGlucoseValue,
    formatGlucoseDelta,
  } from "$lib/utils/formatting";
  import { getDirectionInfo } from "$lib/utils";

  const realtimeStore = getRealtimeStore();

  const STALE_THRESHOLD_MS = 10 * 60 * 1000;

  const units = $derived(glucoseUnits.current);
  const currentBG = $derived(realtimeStore.currentBG);
  const bgDelta = $derived(realtimeStore.bgDelta);
  const lastUpdated = $derived(realtimeStore.lastUpdated);
  const isConnected = $derived(realtimeStore.isConnected);
  const currentTime = $derived(realtimeStore.now);
  const timeSince = $derived(realtimeStore.timeSinceReading);

  const displayBG = $derived(formatGlucoseValue(currentBG, units));
  const displayDelta = $derived(formatGlucoseDelta(bgDelta, units));
  const directionInfo = $derived(getDirectionInfo(realtimeStore.direction));

  const isStale = $derived(currentTime - lastUpdated > STALE_THRESHOLD_MS);
  const isDisconnected = $derived(!isConnected);
  const isDimmed = $derived(isStale || isDisconnected);
  const hasData = $derived(currentBG > 0);

  /** Text color class based on raw BG in mg/dL */
  function getBGTextColor(bg: number): string {
    if (bg < 70) return "text-red-500";
    if (bg < 80) return "text-yellow-500";
    if (bg > 250) return "text-red-500";
    if (bg > 180) return "text-orange-500";
    return "text-green-500";
  }

  const statusText = $derived(isDisconnected ? "Connection Error" : timeSince);
</script>

<div
  class="flex items-center gap-3 border-b px-3 py-2 text-sm"
  class:opacity-50={isDimmed}
>
  {#if hasData}
    <span class="font-mono font-semibold {getBGTextColor(currentBG)}">
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
