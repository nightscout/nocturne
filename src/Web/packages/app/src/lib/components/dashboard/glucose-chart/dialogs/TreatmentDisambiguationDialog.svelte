<script lang="ts">
  import type { EntryRecord } from "$lib/constants/entry-categories";
  import { ENTRY_CATEGORIES } from "$lib/constants/entry-categories";
  import { Badge } from "$lib/components/ui/badge";
  import { Item } from "$lib/components/ui/item";
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
        <Item variant="muted" onclick={() => onSelect(entry)}>
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
          <Badge variant="outline" class="{category.colorClass}">
            {category.name}
          </Badge>
        </Item>
      {/each}
    </div>
    <Dialog.Footer>
      <Button variant="outline" onclick={onClose}>
        Cancel
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
