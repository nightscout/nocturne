<script lang="ts">
  import type { EntryRecord } from "$lib/constants/entry-categories";
  import { ENTRY_CATEGORIES } from "$lib/constants/entry-categories";
  import { Badge } from "$lib/components/ui/badge";
  import * as Dialog from "$lib/components/ui/dialog";

  interface Props {
    open: boolean;
    entries: EntryRecord[];
    onSelect: (entry: EntryRecord) => void;
    onClose: () => void;
  }

  let { open = $bindable(), entries, onSelect, onClose }: Props = $props();

  function formatEntrySummary(entry: EntryRecord): string {
    const parts: string[] = [];
    switch (entry.kind) {
      case "bolus":
        if (entry.data.insulin) parts.push(`${entry.data.insulin}U`);
        if (entry.data.bolusType) parts.push(entry.data.bolusType);
        break;
      case "carbs":
        if (entry.data.carbs) parts.push(`${entry.data.carbs}g carbs`);
        if (entry.data.foodType) parts.push(entry.data.foodType);
        break;
      case "bgCheck":
        if (entry.data.mgdl) parts.push(`${entry.data.mgdl} mg/dL`);
        break;
      case "note":
        if (entry.data.text) parts.push(entry.data.text.slice(0, 50));
        break;
      case "deviceEvent":
        if (entry.data.eventType) parts.push(entry.data.eventType);
        break;
    }
    return parts.join(" · ") || ENTRY_CATEGORIES[entry.kind].name;
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title>Multiple Entries</Dialog.Title>
      <Dialog.Description>
        Several entries occurred around this time. Select one to edit.
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-2 py-2">
      {#each entries as entry (entry.data.id ?? entry.data.mills)}
        {@const category = ENTRY_CATEGORIES[entry.kind]}
        <button
          type="button"
          class="w-full flex items-center gap-3 p-3 rounded-lg bg-muted hover:bg-muted/80 transition-colors text-left"
          onclick={() => onSelect(entry)}
        >
          <div class="flex-1">
            <div class="font-medium text-sm">
              {formatEntrySummary(entry)}
            </div>
            <div class="text-xs text-muted-foreground">
              {entry.data.mills
                ? new Date(entry.data.mills).toLocaleTimeString([], {
                    hour: "numeric",
                    minute: "2-digit",
                  })
                : ""}
            </div>
          </div>
          <Badge variant="outline" class="text-xs {category.colorClass}">
            {category.name}
          </Badge>
        </button>
      {/each}
    </div>
    <Dialog.Footer>
      <button
        type="button"
        class="px-4 py-2 text-sm rounded-md border border-input bg-background hover:bg-accent transition-colors"
        onclick={onClose}
      >
        Cancel
      </button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
