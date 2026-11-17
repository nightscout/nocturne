<script lang="ts">
  import type { Note } from "#lib/api/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Textarea } from "#lib/components/ui/textarea/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import FileText from "@lucide/svelte/icons/file-text";
  import X from "@lucide/svelte/icons/x";

  interface Props {
    note: Partial<Note>;
    onRemove?: () => void;
  }

  let { note = $bindable(), onRemove }: Props = $props();
</script>

<div class="space-y-3">
  <div class="flex items-center justify-between">
    <div class="flex items-center gap-2 text-sm font-medium">
      <FileText class="h-4 w-4 text-muted-foreground" />
      Note
    </div>
    {#if onRemove}
      <Button variant="ghost" size="icon-xs" onclick={onRemove}>
        <X class="h-3.5 w-3.5" />
      </Button>
    {/if}
  </div>

  <div class="space-y-1.5">
    <Label for="note-text">Text</Label>
    <Textarea
      id="note-text"
      bind:value={note.text}
      placeholder="Enter note..."
      rows={3}
    />
  </div>

  <div class="flex items-center gap-2">
    <Checkbox
      id="note-announcement"
      checked={note.isAnnouncement ?? false}
      onCheckedChange={(checked: boolean) => {
        note.isAnnouncement = checked === true;
      }}
    />
    <Label for="note-announcement" variant="option" class="cursor-pointer">
      Is announcement
    </Label>
  </div>
</div>
