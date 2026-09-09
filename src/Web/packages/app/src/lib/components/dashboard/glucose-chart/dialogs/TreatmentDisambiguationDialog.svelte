<script lang="ts">
  import type { EntryRecord } from "$lib/constants/entry-categories";
  import { ENTRY_CATEGORIES } from "$lib/constants/entry-categories";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import * as Dialog from "$lib/components/ui/dialog";
  import { time } from "$lib/utils/formatting";
  import { entrySummary } from "$lib/utils/entry-summary";

  interface Props {
    open: boolean;
    entries: EntryRecord[];
    onSelect: (entry: EntryRecord) => void;
    onClose: () => void;
  }

  let { open = $bindable(), entries, onSelect, onClose }: Props = $props();
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-md print:hidden">
    <Dialog.Header>
      <Dialog.Title>Multiple Entries</Dialog.Title>
      <Dialog.Description>
        Several entries occurred around this time. Select one to edit.
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-2 py-2">
      {#each entries as entry, i (entry.data.id ?? `${entry.data.mills}-${i}`)}
        {@const category = ENTRY_CATEGORIES[entry.kind]}
        <button
          type="button"
          class="w-full flex items-center gap-3 p-3 rounded-lg bg-muted hover:bg-muted/80 transition-colors text-left"
          onclick={() => onSelect(entry)}
        >
          <div class="flex-1">
            <div class="font-medium text-sm">
              {entrySummary(entry)}
            </div>
            <div class="text-xs text-muted-foreground">
              {entry.data.mills
                ? time(entry.data.mills)
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
      <Button variant="outline" onclick={onClose}>
        Cancel
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
