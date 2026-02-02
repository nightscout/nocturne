<script lang="ts">
  import { tick } from "svelte";
  import * as Dialog from "$lib/components/ui/dialog";
  import * as Collapsible from "$lib/components/ui/collapsible";
  import { Button } from "$lib/components/ui/button";
  import { Label } from "$lib/components/ui/label";
  import * as Select from "$lib/components/ui/select";
  import { TextareaAutosize } from "$lib/components/ui/textarea";
  import { Input } from "$lib/components/ui/input";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { CategoryBadge } from "$lib/components/notes";
  import {
    Check,
    ChevronDown,
    StickyNote,
    CheckSquare,
    ExternalLink,
  } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import { goto } from "$app/navigation";
  import { CompletionReason, TrackerCategory, NoteCategory, type Note } from "$api";
  import * as trackersRemote from "$lib/data/trackers.remote";
  import * as treatmentsRemote from "$lib/data/treatments.remote";
  import * as notesRemote from "$lib/data/notes.remote";

  interface TrackerCompletionDialogProps {
    open: boolean;
    instanceId: string | null;
    instanceName?: string;
    /** Category of the tracker definition for default reason selection */
    category?: TrackerCategory;
    /** Definition ID for "Start Another" functionality */
    definitionId?: string;
    /** Event type to create on completion */
    completionEventType?: string;
    /** Default date/time for completion (Date object or YYYY-MM-DD string). If not provided, defaults to now. */
    defaultCompletedAt?: Date | string;
    onClose: () => void;
    onComplete?: () => void;
  }

  let {
    open = $bindable(false),
    instanceId,
    instanceName = "tracker",
    category,
    definitionId,
    completionEventType,
    defaultCompletedAt,
    onClose,
    onComplete,
  }: TrackerCompletionDialogProps = $props();

  let completionReason = $state<CompletionReason>(CompletionReason.Completed);
  let completionNotes = $state("");
  let completedAt = $state("");
  let startAnother = $state(false);
  let isSubmitting = $state(false);

  // Linked notes state
  let linkedNotes = $state<Note[]>([]);
  let notesExpanded = $state(false);
  let viewingNote = $state<Note | null>(null);

  // Get default completion reason based on tracker category
  function getDefaultReasonForCategory(
    cat?: TrackerCategory
  ): CompletionReason {
    switch (cat) {
      case TrackerCategory.Reservoir:
        return CompletionReason.Refilled;
      case TrackerCategory.Appointment:
        return CompletionReason.Attended;
      case TrackerCategory.Sensor:
      case TrackerCategory.Cannula:
      case TrackerCategory.Consumable:
      case TrackerCategory.Battery:
        return CompletionReason.Completed;
      default:
        return CompletionReason.Completed;
    }
  }

  // Format date for datetime-local input (YYYY-MM-DDTHH:mm)
  function formatDateTimeLocal(date: Date): string {
    const pad = (n: number) => n.toString().padStart(2, "0");
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
  }

  // Completion reason labels
  const completionReasonLabels: Record<CompletionReason, string> = {
    [CompletionReason.Completed]: "Completed",
    [CompletionReason.Expired]: "Expired",
    [CompletionReason.Other]: "Other",
    [CompletionReason.Failed]: "Failed",
    [CompletionReason.FellOff]: "Fell Off",
    [CompletionReason.ReplacedEarly]: "Replaced Early",
    [CompletionReason.Empty]: "Empty",
    [CompletionReason.Refilled]: "Refilled",
    [CompletionReason.Attended]: "Attended",
    [CompletionReason.Rescheduled]: "Rescheduled",
    [CompletionReason.Cancelled]: "Cancelled",
    [CompletionReason.Missed]: "Missed",
  };

  // General reasons available to all categories
  const generalReasons: CompletionReason[] = [
    CompletionReason.Completed,
    CompletionReason.Failed,
    CompletionReason.Expired,
    CompletionReason.Other,
  ];

  // Category-specific reasons
  const consumableReasons: CompletionReason[] = [
    CompletionReason.FellOff,
    CompletionReason.ReplacedEarly,
  ];

  const reservoirReasons: CompletionReason[] = [
    CompletionReason.Empty,
    CompletionReason.Refilled,
  ];

  const appointmentReasons: CompletionReason[] = [
    CompletionReason.Attended,
    CompletionReason.Rescheduled,
    CompletionReason.Cancelled,
    CompletionReason.Missed,
  ];

  // Get available completion reasons based on tracker category
  function getReasonsForCategory(cat?: TrackerCategory): CompletionReason[] {
    switch (cat) {
      case TrackerCategory.Reservoir:
        return [...reservoirReasons, ...generalReasons];
      case TrackerCategory.Appointment:
        return [...appointmentReasons, ...generalReasons];
      case TrackerCategory.Sensor:
      case TrackerCategory.Cannula:
      case TrackerCategory.Consumable:
        return [...consumableReasons, ...generalReasons];
      case TrackerCategory.Battery:
        // Battery uses general + failed (device failure)
        return [CompletionReason.Failed, ...generalReasons];
      case TrackerCategory.Reminder:
      case TrackerCategory.Custom:
      default:
        return generalReasons;
    }
  }

  // Reactive list of available reasons based on category
  let availableReasons = $derived(getReasonsForCategory(category));

  // Get default date for completion
  function getDefaultDate(): Date {
    if (!defaultCompletedAt) return new Date();
    if (defaultCompletedAt instanceof Date) return defaultCompletedAt;
    // If it's a YYYY-MM-DD string, parse it and set time to noon to avoid timezone issues
    const parsed = new Date(defaultCompletedAt + "T12:00:00");
    return isNaN(parsed.getTime()) ? new Date() : parsed;
  }

  // Reset form when dialog opens
  $effect(() => {
    if (open) {
      completionReason = getDefaultReasonForCategory(category);
      completionNotes = "";
      completedAt = formatDateTimeLocal(getDefaultDate());
      startAnother = false;
      linkedNotes = [];
      notesExpanded = false;
      viewingNote = null;

      // Fetch linked notes if we have a definition ID
      if (definitionId) {
        fetchLinkedNotes(definitionId);
      }
    }
  });

  async function fetchLinkedNotes(trackerId: string) {
    try {
      const notes = await notesRemote.getNotes({ trackerDefinitionId: trackerId });
      linkedNotes = notes || [];
      // Auto-expand if there are notes
      if (linkedNotes.length > 0) {
        notesExpanded = true;
      }
    } catch (err) {
      console.error("Failed to fetch linked notes:", err);
    }
  }

  function getChecklistProgress(note: Note): string | null {
    const items = note.checklistItems;
    if (!items || items.length === 0) return null;
    const completed = items.filter((i) => i.isCompleted).length;
    return `${completed}/${items.length}`;
  }

  async function handleComplete() {
    if (!instanceId) return;
    isSubmitting = true;
    try {
      await trackersRemote.completeInstance({
        id: instanceId,
        request: {
          reason: completionReason,
          completionNotes: completionNotes || undefined,
          completedAt: completedAt ? new Date(completedAt) : undefined,
        },
      });

      // If "Start Another" is checked and we have a definitionId, start a new instance
      if (startAnother && definitionId) {
        await trackersRemote.startInstance({
          definitionId,
          startedAt: completedAt ? new Date(completedAt) : undefined,
        });
      }

      // Create treatment event if configured
      if (completionEventType) {
        await treatmentsRemote.createTreatment({
          eventType: completionEventType,
          created_at: completedAt
            ? new Date(completedAt).toISOString()
            : new Date().toISOString(),
          notes: completionNotes || undefined,
          enteredBy: "Nocturne Tracker",
        });
      }

      open = false;
      await tick();
      onComplete?.();
    } catch (err) {
      console.error("Failed to complete tracker:", err);
    } finally {
      isSubmitting = false;
    }
  }

  function handleClose() {
    open = false;
    onClose();
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content>
    <Dialog.Header>
      <Dialog.Title>Complete {instanceName}</Dialog.Title>
      <Dialog.Description>
        Mark this tracker as complete with an optional reason and notes.
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      <!-- Linked Notes Section -->
      {#if linkedNotes.length > 0}
        <Collapsible.Root bind:open={notesExpanded}>
          <Collapsible.Trigger
            class="flex items-center justify-between w-full p-3 rounded-lg border bg-muted/30 hover:bg-muted/50 transition-colors"
          >
            <div class="flex items-center gap-2">
              <StickyNote class="h-4 w-4 text-muted-foreground" />
              <span class="font-medium text-sm">
                Linked Notes ({linkedNotes.length})
              </span>
            </div>
            <ChevronDown
              class={cn(
                "h-4 w-4 text-muted-foreground transition-transform",
                notesExpanded && "rotate-180"
              )}
            />
          </Collapsible.Trigger>
          <Collapsible.Content>
            <div class="space-y-2 mt-2">
              {#each linkedNotes as note (note.id)}
                {@const progress = getChecklistProgress(note)}
                <button
                  type="button"
                  class="w-full text-left p-3 rounded-lg border bg-card hover:bg-muted/30 transition-colors"
                  onclick={() => (viewingNote = note)}
                >
                  <div class="flex items-start gap-2">
                    <CategoryBadge
                      category={note.category ?? NoteCategory.Observation}
                      showLabel={false}
                      class="shrink-0"
                    />
                    <div class="flex-1 min-w-0">
                      <div class="flex items-center gap-2">
                        {#if note.title}
                          <span class="font-medium text-sm truncate">{note.title}</span>
                        {:else}
                          <span class="text-sm text-muted-foreground line-clamp-1">
                            {note.content}
                          </span>
                        {/if}
                        {#if progress}
                          <span class="text-xs text-muted-foreground flex items-center gap-1">
                            <CheckSquare class="h-3 w-3" />
                            {progress}
                          </span>
                        {/if}
                      </div>
                      {#if note.title && note.content}
                        <p class="text-xs text-muted-foreground line-clamp-1 mt-0.5">
                          {note.content}
                        </p>
                      {/if}
                    </div>
                  </div>
                </button>
              {/each}
            </div>
          </Collapsible.Content>
        </Collapsible.Root>
      {/if}
      <div class="space-y-2">
        <Label for="completedAt">Completed At</Label>
        <Input
          id="completedAt"
          type="datetime-local"
          bind:value={completedAt}
        />
      </div>
      <div class="space-y-2">
        <Label for="reason">Completion Reason</Label>
        <Select.Root type="single" bind:value={completionReason}>
          <Select.Trigger>
            {completionReasonLabels[completionReason]}
          </Select.Trigger>
          <Select.Content>
            {#each availableReasons as reason}
              <Select.Item value={reason} label={completionReasonLabels[reason]} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="completionNotes">Notes (optional)</Label>
        <TextareaAutosize
          id="completionNotes"
          bind:value={completionNotes}
          placeholder="e.g., Sensor error E2 on day 8"
        />
      </div>
      {#if definitionId}
        <div class="flex items-center gap-2">
          <Checkbox id="startAnother" bind:checked={startAnother} />
          <Label for="startAnother" class="text-sm font-normal cursor-pointer">
            Start another {instanceName} after completion
          </Label>
        </div>
      {/if}
    </div>
    <Dialog.Footer>
      <Button variant="outline" onclick={handleClose} disabled={isSubmitting}>
        Cancel
      </Button>
      <Button onclick={handleComplete} disabled={isSubmitting}>
        <Check class="h-4 w-4 mr-2" />
        Complete
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Nested Note Detail Dialog -->
<Dialog.Root open={!!viewingNote} onOpenChange={(open) => !open && (viewingNote = null)}>
  <Dialog.Content class="sm:max-w-[500px]">
    {#if viewingNote}
      <Dialog.Header>
        <Dialog.Title class="flex items-center gap-2">
          <CategoryBadge category={viewingNote.category ?? NoteCategory.Observation} />
          {viewingNote.title || "Note"}
        </Dialog.Title>
        {#if viewingNote.occurredAt}
          <Dialog.Description>
            {new Date(viewingNote.occurredAt).toLocaleDateString(undefined, {
              weekday: "long",
              year: "numeric",
              month: "long",
              day: "numeric",
            })}
          </Dialog.Description>
        {/if}
      </Dialog.Header>

      <div class="py-4 space-y-4">
        <p class="text-sm whitespace-pre-wrap">{viewingNote.content}</p>

        {#if viewingNote.checklistItems && viewingNote.checklistItems.length > 0}
          <div class="border-t pt-4">
            <h4 class="text-sm font-medium mb-2">Checklist</h4>
            <ul class="space-y-1">
              {#each viewingNote.checklistItems as item}
                <li class="flex items-center gap-2 text-sm">
                  <span
                    class={cn(
                      "w-4 h-4 rounded border flex items-center justify-center text-xs",
                      item.isCompleted
                        ? "bg-primary text-primary-foreground"
                        : "border-muted-foreground"
                    )}
                  >
                    {#if item.isCompleted}✓{/if}
                  </span>
                  <span class={item.isCompleted ? "line-through text-muted-foreground" : ""}>
                    {item.text}
                  </span>
                </li>
              {/each}
            </ul>
          </div>
        {/if}
      </div>

      <Dialog.Footer>
        <Button variant="outline" onclick={() => (viewingNote = null)}>
          Back
        </Button>
        <Button
          variant="default"
          onclick={() => {
            const noteId = viewingNote?.id;
            viewingNote = null;
            if (noteId) {
              goto(`/notes?edit=${noteId}`);
            }
          }}
        >
          <ExternalLink class="h-4 w-4 mr-2" />
          Edit Note
        </Button>
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>
