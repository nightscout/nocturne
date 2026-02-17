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
  import { formatTime } from "$lib/utils";
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
    title = "Recent Entries",
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

  function getEntryLabel(entry: EntryRecord): string {
    switch (entry.kind) {
      case "bolus":
        return entry.data.insulin ? `${entry.data.insulin}u insulin` : "Bolus";
      case "carbs":
        return entry.data.carbs ? `${entry.data.carbs}g carbs` : "Carbs";
      case "bgCheck":
        return entry.data.mgdl ? `${entry.data.mgdl} mg/dL` : "BG Check";
      case "note":
        return entry.data.text ?? "Note";
      case "deviceEvent":
        return entry.data.eventType ?? "Device Event";
    }
  }

  function getEntryDetails(entry: EntryRecord): string {
    switch (entry.kind) {
      case "bolus":
        return entry.data.bolusType ?? "";
      case "carbs":
        return entry.data.foodType ?? "";
      case "bgCheck":
        return entry.data.glucoseType ?? "";
      case "note":
        return entry.data.isAnnouncement ? "Announcement" : "";
      case "deviceEvent":
        return entry.data.notes ?? "";
    }
  }
</script>

<Card class="@container">
  <svelte:boundary>
    {#snippet pending()}
      <div class="flex items-center justify-center h-full">
        <div
          class="animate-spin rounded-full h-8 w-8 border-b-2 border-gray-900"
        ></div>
      </div>
    {/snippet}
    {#snippet failed(_error)}
      <p class="text-red-500 text-center">Error loading recent entries.</p>
    {/snippet}
    <CardHeader class="px-3 @md:px-6">
      <CardTitle>{title}</CardTitle>
      <p class="text-sm text-muted-foreground">{subtitle}</p>
    </CardHeader>
    <CardContent class="px-3 @md:px-6">
      {#if displayEntries.length > 0}
        <div class="space-y-2 @md:space-y-3">
          {#each displayEntries as entry (entry.data.id ?? entry.data.mills)}
            {@const category = ENTRY_CATEGORIES[entry.kind]}
            <div
              class="flex items-center justify-between p-2 @md:p-3 bg-muted rounded-lg cursor-pointer hover:bg-muted/80 transition-colors"
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
                <Badge variant="outline" class="text-xs @md:text-sm {category.colorClass}">
                  {category.name}
                </Badge>
                <div>
                  <div class="font-medium">
                    {getEntryLabel(entry)}
                    {#if getEntryDetails(entry)}
                      <span class="text-muted-foreground"> - {getEntryDetails(entry)}</span>
                    {/if}
                  </div>
                  <div class="text-sm text-muted-foreground">
                    {formatTime(entry.data.mills!)}
                  </div>
                </div>
              </div>
              <div class="text-sm text-muted-foreground">
                {entry.data.dataSource || ""}
              </div>
            </div>
          {/each}
        </div>
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
