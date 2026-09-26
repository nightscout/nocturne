<script lang="ts">
  import type { EntryRecord } from "$lib/constants/entry-categories";
  import { ENTRY_CATEGORIES } from "$lib/constants/entry-categories";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { time } from "$lib/utils/formatting";
  import { entryDetails, entryLabel } from "$lib/utils/entry-summary";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { EntryEditDialog } from "$lib/components/entries";

  interface ComponentProps {
    entries?: EntryRecord[];
    maxEntries?: number;
    title?: string;
    subtitle?: string;
  }

  let {
    entries,
    maxEntries = 5,
    title = "Recent treatments",
    subtitle = "Last 24 hours",
  }: ComponentProps = $props();

  const realtimeStore = getRealtimeStore();

  const displayEntries = $derived(
    (entries ?? realtimeStore.recentEntries).slice(0, maxEntries),
  );

  let selectedEntry = $state<EntryRecord | null>(null);
  let correlatedRecords = $state<EntryRecord[]>([]);
  let isDialogOpen = $state(false);

  function handleEntryClick(entry: EntryRecord) {
    selectedEntry = entry;
    correlatedRecords = realtimeStore.findCorrelatedEntries(entry);
    isDialogOpen = true;
  }

</script>

<Card class="@container">
  <svelte:boundary>
    {#snippet pending()}
      <div class="flex items-center justify-center h-full">
        <div
          class="animate-spin rounded-full h-8 w-8 border-b-2 border-foreground"
        ></div>
      </div>
    {/snippet}
    {#snippet failed(_error)}
      <p class="text-destructive text-center">Error loading recent entries.</p>
    {/snippet}
    <CardHeader class="px-3 @md:px-6">
      <CardTitle>{title}</CardTitle>
      <p class="text-sm text-muted-foreground">{subtitle}</p>
    </CardHeader>
    <CardContent class="px-3 @md:px-6">
      {#if displayEntries.length > 0}
        <ul class="m-0 list-none divide-y divide-border p-0">
          {#each displayEntries as entry, i (entry.data.id ?? `${entry.data.mills}-${i}`)}
            {@const category = ENTRY_CATEGORIES[entry.kind]}
            <li>
            <div
              class="-mx-2 flex cursor-pointer items-center justify-between rounded-md px-2 py-2.5 transition-colors hover:bg-accent/50"
              onclick={() => handleEntryClick(entry)}
              role="button"
              tabindex="0"
              onkeydown={(e) => {
                if (e.key === "Enter" || e.key === " ") {
                  e.preventDefault();
                  handleEntryClick(entry);
                }
              }}
            >
              <div class="flex items-center gap-2 @md:gap-3">
                <Badge variant={category.badge}>
                  {category.name}
                </Badge>
                <div>
                  <div class="font-medium">
                    {entryLabel(entry)}
                    {#if entryDetails(entry)}
                      <span class="text-muted-foreground"> - {entryDetails(entry)}</span>
                    {/if}
                  </div>
                  <div class="text-sm text-muted-foreground">
                    {time(entry.data.mills!)}
                  </div>
                </div>
              </div>
              <div class="text-sm text-muted-foreground">
                {entry.data.dataSource || ""}
              </div>
            </div>
            </li>
          {/each}
        </ul>
      {:else}
        <p class="text-muted-foreground text-center py-8">
          No recent entries
        </p>
      {/if}
    </CardContent>
  </svelte:boundary>
</Card>

<EntryEditDialog
  bind:open={isDialogOpen}
  entry={selectedEntry}
  {correlatedRecords}
  onClose={() => {
    isDialogOpen = false;
    selectedEntry = null;
    correlatedRecords = [];
  }}
/>
