<script lang="ts">
  import type { Entry } from "$lib/api";
  import { Card, CardContent, CardHeader } from "$lib/components/ui/card";

  import { formatTime } from "$lib/utils";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import {
    formatGlucoseValue,
    formatGlucoseDelta,
    getUnitLabel,
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
  <CardHeader class="px-3 @md:px-6">Recent Entries</CardHeader>
  <CardContent class="px-3 @md:px-6">
    {#if recentEntries.length > 0}
      <div class="space-y-2 @md:space-y-3">
        {#each recentEntries as entry (entry._id || entry.mills)}
          {@const directionInfo = getDirectionInfo(entry.direction)}
          {@const Icon = directionInfo.icon}
          <div
            class="flex items-center justify-between p-2 @md:p-3 bg-muted rounded-lg"
          >
            <div class="flex items-center gap-2 @md:gap-3">
              <div>
                <div class="font-medium">
                  {#if entry.sgv}
                    {formatGlucoseValue(entry.sgv, units)} {unitLabel}
                  {/if}
                  {#if entry.notes}
                    - {entry.notes}
                  {/if}
                </div>
                <div class="text-sm text-muted-foreground">
                  {formatTime(entry.mills!)}
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
          </div>
        {/each}
      </div>
    {:else}
      <p class="text-muted-foreground text-center py-8">No recent entries</p>
    {/if}
  </CardContent>
</Card>
