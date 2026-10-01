<script lang="ts">
  import type { Entry } from "$lib/api";
  import { Card, CardContent, CardHeader, CardTitle } from "$lib/components/ui/card";

  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import {
    formatGlucoseValue,
    formatGlucoseDelta,
    getUnitLabel,
    time,
  } from "$lib/utils/formatting";
  import { getDirectionInfo } from "$lib/utils";

  interface ComponentProps {
    entries?: Entry[];
    maxEntries?: number;
  }

  let { entries, maxEntries = 5 }: ComponentProps = $props();

  const realtimeStore = getRealtimeStore();

  // Use realtime store entries as fallback when entries prop not provided
  const displayEntries = $derived(entries ?? realtimeStore.entries);
  const recentEntries = $derived(displayEntries.slice(0, maxEntries));

  // Get units preference
  const units = $derived(glucoseUnits.current);
  const unitLabel = $derived(getUnitLabel(units));
</script>

<Card class="@container">
  <CardHeader class="px-3 @md:px-6">
    <CardTitle>Recent readings</CardTitle>
  </CardHeader>
  <CardContent class="px-3 @md:px-6">
    {#if recentEntries.length > 0}
      <ul class="m-0 list-none divide-y divide-border p-0">
        {#each recentEntries as entry, i (entry._id || `${entry.mills}-${i}`)}
          {@const directionInfo = getDirectionInfo(entry.direction)}
          {@const Icon = directionInfo.icon}
          <li class="flex items-center justify-between py-2.5">
            <div class="flex items-center gap-2 @md:gap-3">
              <div>
                <div class="font-medium tabular-nums">
                  {#if entry.sgv}
                    {formatGlucoseValue(entry.sgv, units)} {unitLabel}
                  {/if}
                  {#if entry.notes}
                    - {entry.notes}
                  {/if}
                </div>
                <div class="text-sm text-muted-foreground">
                  {time(entry.mills!)}
                </div>
              </div>
            </div>
            <div class="flex items-center gap-2 text-sm text-muted-foreground">
              {#if entry.delta !== undefined}
                {formatGlucoseDelta(entry.delta, units)}
              {:else}
                —
              {/if}
              <Icon class="h-4 w-4 {directionInfo.css}" />
            </div>
          </li>
        {/each}
      </ul>
    {:else}
      <p class="text-muted-foreground text-center py-8">No recent entries</p>
    {/if}
  </CardContent>
</Card>
