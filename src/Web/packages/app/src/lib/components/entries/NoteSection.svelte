<script lang="ts">
  import type { Note } from "$lib/api";
  import { Label } from "$lib/components/ui/label";
  import { Button } from "$lib/components/ui/button";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { FileText, X } from "lucide-svelte";

  interface Props {
    note: Partial<Note>;
    onRemove?: () => void;
  }

  let { note = $bindable(), onRemove }: Props = $props();
</script>

<div class="space-y-3">
  <div class="flex items-center justify-between">
    <div class="flex items-center gap-2 text-sm font-medium">
      <FileText class="h-4 w-4 text-amber-500" />
      Note
    </div>
    {#if onRemove}
      <Button variant="ghost" size="icon" class="h-6 w-6" onclick={onRemove}>
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
      onCheckedChange={(checked) => {
        note.isAnnouncement = checked === true;
      }}
    />
    <Label for="note-announcement" class="text-sm font-normal cursor-pointer">
      Is Announcement
    </Label>
  </div>
</div>
