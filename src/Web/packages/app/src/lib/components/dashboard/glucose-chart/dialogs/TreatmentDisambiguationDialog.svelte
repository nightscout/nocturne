<script lang="ts">
  import type { EntryRecord } from "#lib/constants/entry-categories.js";
  import { ENTRY_CATEGORIES } from "#lib/constants/entry-categories.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Item } from "#lib/components/ui/item/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { time } from "#lib/utils/formatting.js";
  import { entrySummary } from "#lib/utils/entry-summary.js";

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
      <Dialog.Title>Multiple entries</Dialog.Title>
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
          <Badge variant={category.badge}>
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
