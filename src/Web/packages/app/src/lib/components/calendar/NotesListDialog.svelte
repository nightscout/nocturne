<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import {
    Eye,
    HelpCircle,
    CheckSquare,
    Flag,
    Link,
    Calendar,
  } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import { NoteCategory, type Note } from "$api";

  export interface NoteEvent {
    note: Note;
    source: "occurredAt" | "trackerLink";
    trackerName?: string;
    /** The reminder time from linked tracker's expectedEndAt */
    reminderTime?: Date;
  }

  interface Props {
    /** Whether the dialog is open */
    open?: boolean;
    /** Callback when open state changes */
    onOpenChange?: (open: boolean) => void;
    /** The date being displayed */
    date: Date;
    /** Notes to display */
    noteEvents?: NoteEvent[];
    /** Callback when a note is clicked */
    onNoteClick?: (note: Note) => void;
  }

  let {
    open = $bindable(false),
    onOpenChange,
    date,
    noteEvents = [],
    onNoteClick,
  }: Props = $props();

  // Category colors
  const categoryColors = {
    [NoteCategory.Observation]: "text-blue-500 bg-blue-500/10",
    [NoteCategory.Question]: "text-purple-500 bg-purple-500/10",
    [NoteCategory.Task]: "text-green-500 bg-green-500/10",
    [NoteCategory.Marker]: "text-orange-500 bg-orange-500/10",
  };

  // Format date for display
  const formattedDate = $derived(
    date.toLocaleDateString(undefined, {
      weekday: "long",
      month: "long",
      day: "numeric",
    })
  );

  function handleNoteClick(note: Note) {
    onNoteClick?.(note);
  }

  function handleOpenChange(newOpen: boolean) {
    open = newOpen;
    onOpenChange?.(newOpen);
  }

  // Get checklist progress
  function getChecklistProgress(note: Note): string | null {
    const items = note.checklistItems;
    if (!items || items.length === 0) return null;
    const completed = items.filter((i) => i.isCompleted).length;
    return `${completed}/${items.length}`;
  }
</script>

<Dialog.Root bind:open onOpenChange={handleOpenChange}>
  <Dialog.Content class="sm:max-w-[425px] max-h-[80vh] overflow-hidden flex flex-col">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        <Calendar class="h-5 w-5" />
        Notes for {formattedDate}
      </Dialog.Title>
      <Dialog.Description>
        {noteEvents.length} note{noteEvents.length !== 1 ? "s" : ""} on this day
      </Dialog.Description>
    </Dialog.Header>

    <div class="flex-1 overflow-y-auto py-2">
      {#if noteEvents.length === 0}
        <p class="text-center text-muted-foreground py-8">
          No notes for this day
        </p>
      {:else}
        <div class="space-y-2">
          {#each noteEvents as { note, source, trackerName }, idx (`${note.id}-${source}-${trackerName ?? idx}`)}
            {@const category = note.category ?? NoteCategory.Observation}
            {@const colorClass = categoryColors[category]}
            {@const progress = getChecklistProgress(note)}

            <button
              type="button"
              class="w-full text-left p-3 rounded-lg border bg-card hover:bg-muted/50 transition-colors"
              onclick={() => handleNoteClick(note)}
            >
              <div class="flex items-start gap-3">
                <div class={cn("p-2 rounded-lg shrink-0", colorClass)}>
                  {#if category === NoteCategory.Observation}
                    <Eye class="h-4 w-4" />
                  {:else if category === NoteCategory.Question}
                    <HelpCircle class="h-4 w-4" />
                  {:else if category === NoteCategory.Task}
                    <CheckSquare class="h-4 w-4" />
                  {:else if category === NoteCategory.Marker}
                    <Flag class="h-4 w-4" />
                  {/if}
                </div>

                <div class="flex-1 min-w-0">
                  {#if note.title}
                    <h4 class="font-medium text-sm truncate">{note.title}</h4>
                  {/if}
                  <p
                    class={cn(
                      "text-sm line-clamp-2",
                      note.title ? "text-muted-foreground" : "font-medium"
                    )}
                  >
                    {note.content}
                  </p>

                  <div class="flex items-center gap-2 mt-2 flex-wrap">
                    {#if source === "trackerLink" && trackerName}
                      <Badge variant="outline" class="text-xs gap-1">
                        <Link class="h-3 w-3" />
                        {trackerName}
                      </Badge>
                    {/if}
                    {#if progress}
                      <Badge variant="secondary" class="text-xs gap-1">
                        <CheckSquare class="h-3 w-3" />
                        {progress}
                      </Badge>
                    {/if}
                  </div>
                </div>
              </div>
            </button>
          {/each}
        </div>
      {/if}
    </div>

    <Dialog.Footer>
      <Button variant="outline" onclick={() => handleOpenChange(false)}>
        Close
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
