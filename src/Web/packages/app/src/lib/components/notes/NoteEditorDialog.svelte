<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import * as Dialog from "$lib/components/ui/dialog";
  import * as Select from "$lib/components/ui/select";
  import {
    Plus,
    Trash2,
    Loader2,
    ImagePlus,
    X,
    Link,
    Unlink,
    ChevronDown,
  } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import { formatDateForInput } from "$lib/utils/date-formatting";
  import * as notesRemote from "$lib/data/notes.remote";
  import * as trackersRemote from "$lib/data/trackers.remote";
  import { TrackerCategoryIcon } from "$lib/components/icons";
  import { ThresholdEditor } from "$lib/components/ui/threshold-editor";
  import { TrackerLinkSection } from "$lib/components/notes";
  import { categoryConfig } from "$lib/components/notes/category-config";
  import {
    NoteCategory,
    TrackerCategory,
    TrackerMode,
    type Note,
    type TrackerDefinitionDto,
  } from "$api";
  import { onMount } from "svelte";

  interface Props {
    /** Whether the dialog is open */
    open?: boolean;
    /** The note being edited, or null for a new note */
    note?: Note | null;
    /** Default category for new notes */
    defaultCategory?: NoteCategory;
    /** Default date/time for new notes */
    defaultDate?: Date;
    /** Callback when dialog closes */
    onClose?: () => void;
    /** Callback after note is saved */
    onSave?: (note: Note) => void;
    /** Callback after note is deleted */
    onDelete?: (noteId: string) => void;
  }

  let {
    open = $bindable(false),
    note = null,
    defaultCategory = NoteCategory.Observation,
    defaultDate,
    onClose,
    onSave,
    onDelete,
  }: Props = $props();

  // State
  let trackerDefinitions = $state<TrackerDefinitionDto[]>([]);
  let loadingDefinitions = $state(false);

  // Form state
  let formTitle = $state("");
  let formContent = $state("");
  let formCategory = $state<NoteCategory>(NoteCategory.Observation);
  let formChecklistItems = $state<
    Array<{ id?: string; text: string; isCompleted: boolean }>
  >([]);
  let formOccurredAt = $state("");
  let newChecklistItemText = $state("");

  // Attachment state for new notes
  let pendingAttachments = $state<File[]>([]);
  let uploadingAttachment = $state(false);
  let formTrackerLinks = $state<
    Array<{
      trackerDefinitionId: string;
      thresholds: Array<{
        hoursOffset: number | undefined;
        description?: string;
      }>;
    }>
  >([]);
  let expandedNewLinks = $state<Set<string>>(new Set());

  // Derived state
  const isNewNote = $derived(note === null);
  const currentNote = $state.raw<Note | null>(null);

  // Available definitions for new note
  const availableDefinitionsForNewNote = $derived(
    trackerDefinitions.filter(
      (d) => !formTrackerLinks.some((l) => l.trackerDefinitionId === d.id)
    )
  );

  // Load tracker definitions on mount
  onMount(async () => {
    loadingDefinitions = true;
    try {
      const defs = await trackersRemote.getDefinitions(undefined);
      trackerDefinitions = defs || [];
    } catch (err) {
      console.error("Failed to load tracker definitions:", err);
    } finally {
      loadingDefinitions = false;
    }
  });

  // Reset form when dialog opens or note changes
  $effect(() => {
    if (open) {
      if (note) {
        // Editing existing note
        formTitle = note.title ?? "";
        formContent = note.content ?? "";
        formCategory = note.category ?? NoteCategory.Observation;
        formChecklistItems = (note.checklistItems ?? []).map((item) => ({
          id: item.id,
          text: item.text ?? "",
          isCompleted: item.isCompleted ?? false,
        }));
        formOccurredAt = note.occurredAt
          ? formatDateForInput(new Date(note.occurredAt).toISOString())
          : formatDateForInput(new Date().toISOString());
        formTrackerLinks = [];
      } else {
        // New note
        formTitle = "";
        formContent = "";
        formCategory = defaultCategory;
        formChecklistItems = [];
        const dateToUse = defaultDate ?? new Date();
        formOccurredAt = formatDateForInput(dateToUse.toISOString());
        formTrackerLinks = [];
      }
      newChecklistItemText = "";
      pendingAttachments = [];
      expandedNewLinks = new Set();
    }
  });

  // Get definition by ID
  function getDefinition(id: string): TrackerDefinitionDto | undefined {
    return trackerDefinitions.find((d) => d.id === id);
  }

  // Add tracker link for new note
  function addFormTrackerLink(definitionId: string) {
    formTrackerLinks = [
      ...formTrackerLinks,
      { trackerDefinitionId: definitionId, thresholds: [] },
    ];
    expandedNewLinks.add(definitionId);
    expandedNewLinks = new Set(expandedNewLinks);
  }

  // Remove tracker link for new note
  function removeFormTrackerLink(definitionId: string) {
    formTrackerLinks = formTrackerLinks.filter(
      (l) => l.trackerDefinitionId !== definitionId
    );
    expandedNewLinks.delete(definitionId);
    expandedNewLinks = new Set(expandedNewLinks);
  }

  // Toggle expansion for new note links
  function toggleNewLinkExpanded(definitionId: string) {
    if (expandedNewLinks.has(definitionId)) {
      expandedNewLinks.delete(definitionId);
    } else {
      expandedNewLinks.add(definitionId);
    }
    expandedNewLinks = new Set(expandedNewLinks);
  }

  // Update thresholds for new note link
  function updateFormLinkThresholds(
    definitionId: string,
    thresholds: Record<string, unknown>[]
  ) {
    formTrackerLinks = formTrackerLinks.map((l) =>
      l.trackerDefinitionId === definitionId
        ? {
            ...l,
            thresholds: thresholds.map((t) => ({
              hoursOffset: t.hoursOffset as number | undefined,
              description: t.description as string | undefined,
            })),
          }
        : l
    );
  }

  // Create threshold for new note
  function createNewNoteThreshold() {
    return { hoursOffset: undefined, description: "" };
  }

  // Reload the current note being edited
  async function reloadEditingNote() {
    if (!note?.id) return;
    try {
      const updated = await notesRemote.getNote(note.id);
      if (updated && onSave) {
        onSave(updated);
      }
    } catch (err) {
      console.error("Failed to reload note:", err);
    }
  }

  // Link tracker to note (editing mode)
  async function handleLinkTracker(
    trackerDefinitionId: string,
    thresholds: Array<{ hoursOffset: number | undefined; description?: string }>
  ) {
    if (!note?.id) return;
    try {
      await notesRemote.linkTracker({
        noteId: note.id,
        trackerDefinitionId,
        thresholds: thresholds.map((t) => ({
          hoursOffset: t.hoursOffset,
          description: t.description,
        })),
      });
      await reloadEditingNote();
    } catch (err) {
      console.error("Failed to link tracker:", err);
    }
  }

  // Unlink tracker from note (editing mode)
  async function handleUnlinkTracker(linkId: string) {
    if (!note?.id) return;
    try {
      await notesRemote.unlinkTracker({
        noteId: note.id,
        linkId,
      });
      await reloadEditingNote();
    } catch (err) {
      console.error("Failed to unlink tracker:", err);
    }
  }

  // Update thresholds for a tracker link
  async function handleUpdateThresholds(
    linkId: string,
    thresholds: Array<{ hoursOffset: number | undefined; description?: string }>
  ) {
    if (!note?.id) return;
    const link = note.trackerLinks?.find((l) => l.id === linkId);
    if (!link?.trackerDefinitionId) return;

    try {
      await notesRemote.unlinkTracker({
        noteId: note.id,
        linkId,
      });
      await notesRemote.linkTracker({
        noteId: note.id,
        trackerDefinitionId: link.trackerDefinitionId,
        thresholds: thresholds.map((t) => ({
          hoursOffset: t.hoursOffset,
          description: t.description,
        })),
      });
      await reloadEditingNote();
    } catch (err) {
      console.error("Failed to update thresholds:", err);
    }
  }

  // Close dialog
  function handleClose() {
    open = false;
    onClose?.();
  }

  // Save note
  async function saveNote() {
    try {
      const noteData = {
        category: formCategory,
        title: formTitle,
        content: formContent || undefined,
        occurredAt: formOccurredAt ? new Date(formOccurredAt) : undefined,
        checklistItems:
          formChecklistItems.length > 0
            ? formChecklistItems.map((item, index) => ({
                id: item.id,
                text: item.text,
                isCompleted: item.isCompleted,
                sortOrder: index,
              }))
            : undefined,
      };

      let savedNote: Note | undefined;

      if (isNewNote) {
        savedNote = await notesRemote.createNoteWithTrackerLinks({
          ...noteData,
          trackerLinks:
            formTrackerLinks.length > 0 ? formTrackerLinks : undefined,
        });
      } else if (note) {
        await notesRemote.updateNote({
          id: note.id!,
          ...noteData,
        });
        savedNote = await notesRemote.getNote(note.id!);
      }

      // Upload any pending attachments
      if (savedNote?.id && pendingAttachments.length > 0) {
        await uploadAttachments(savedNote.id);
      }

      if (savedNote) {
        onSave?.(savedNote);
      }
      handleClose();
    } catch (err) {
      console.error("Failed to save note:", err);
    }
  }

  // Add checklist item from the always-visible input
  function addChecklistItemFromInput() {
    if (!newChecklistItemText.trim()) return;
    formChecklistItems = [
      ...formChecklistItems,
      { text: newChecklistItemText.trim(), isCompleted: false },
    ];
    newChecklistItemText = "";
  }

  // Remove checklist item from form
  function removeChecklistItem(index: number) {
    formChecklistItems = formChecklistItems.filter((_, i) => i !== index);
  }

  // Handle file selection for attachments
  function handleFileSelect(event: Event) {
    const input = event.target as HTMLInputElement;
    if (!input.files) return;

    const files = Array.from(input.files);
    const imageFiles = files.filter((f) => f.type.startsWith("image/"));
    pendingAttachments = [...pendingAttachments, ...imageFiles];

    input.value = "";
  }

  // Remove pending attachment
  function removePendingAttachment(index: number) {
    pendingAttachments = pendingAttachments.filter((_, i) => i !== index);
  }

  // Delete existing attachment
  async function deleteExistingAttachment(
    noteId: string,
    attachmentId: string
  ) {
    try {
      await notesRemote.deleteAttachment({ noteId, attachmentId });
      await reloadEditingNote();
    } catch (err) {
      console.error("Failed to delete attachment:", err);
    }
  }

  // Upload attachments
  async function uploadAttachments(noteId: string) {
    if (pendingAttachments.length === 0) return;

    uploadingAttachment = true;
    try {
      for (const file of pendingAttachments) {
        await notesRemote.uploadAttachment({
          noteId,
          file: { data: file, fileName: file.name },
        });
      }
      pendingAttachments = [];
    } catch (err) {
      console.error("Failed to upload attachments:", err);
    } finally {
      uploadingAttachment = false;
    }
  }
</script>

<Dialog.Root bind:open onOpenChange={(o) => !o && handleClose()}>
  <Dialog.Content class="sm:max-w-[500px] max-h-[90vh] overflow-y-auto">
    <Dialog.Header>
      <Dialog.Title>
        {isNewNote ? "New Note" : "Edit Note"}
      </Dialog.Title>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      <!-- Category -->
      <div class="space-y-2">
        <Label>Category</Label>
        <Select.Root type="single" bind:value={formCategory}>
          <Select.Trigger>
            {#if formCategory}
              {@const config = categoryConfig[formCategory]}
              {@const Icon = config.icon}
              <span class="flex items-center gap-2">
                <Icon class="h-4 w-4" />
                {config.label}
              </span>
            {:else}
              Select category
            {/if}
          </Select.Trigger>
          <Select.Content>
            {#each Object.values(NoteCategory) as category}
              {@const config = categoryConfig[category]}
              {@const Icon = config.icon}
              <Select.Item value={category}>
                <span class="flex items-center gap-2">
                  <Icon class="h-4 w-4" />
                  {config.label}
                </span>
              </Select.Item>
            {/each}
          </Select.Content>
        </Select.Root>
        {#if formCategory}
          <p class="text-xs text-muted-foreground">
            {categoryConfig[formCategory].description}
          </p>
        {/if}
      </div>

      <!-- Date/Time -->
      <div class="space-y-2">
        <Label for="occurredAt">Date & Time</Label>
        <Input
          id="occurredAt"
          type="datetime-local"
          bind:value={formOccurredAt}
        />
      </div>

      <!-- Title -->
      <div class="space-y-2">
        <Label for="title">Title</Label>
        <Input
          id="title"
          bind:value={formTitle}
          placeholder="Give your note a title..."
          required
        />
      </div>

      <!-- Content -->
      <div class="space-y-2">
        <Label for="content">Content (optional)</Label>
        <Textarea
          id="content"
          bind:value={formContent}
          placeholder="Add additional details..."
          rows={4}
        />
      </div>

      <!-- Checklist Items -->
      <div class="space-y-2">
        <Label>Checklist Items</Label>
        {#if formChecklistItems.length > 0}
          <div class="space-y-2">
            {#each formChecklistItems as item, index (index)}
              <div class="flex items-center gap-2">
                <Checkbox
                  checked={item.isCompleted}
                  onCheckedChange={(checked) =>
                    (item.isCompleted = checked === true)}
                />
                <Input
                  bind:value={item.text}
                  placeholder="Checklist item..."
                  class="flex-1"
                />
                <Button
                  variant="ghost"
                  size="icon"
                  class="h-8 w-8 shrink-0"
                  onclick={() => removeChecklistItem(index)}
                >
                  <Trash2 class="h-4 w-4" />
                </Button>
              </div>
            {/each}
          </div>
        {/if}
        <div class="flex items-center gap-2">
          <Checkbox disabled class="opacity-50" />
          <Input
            bind:value={newChecklistItemText}
            placeholder="Add checklist item..."
            class="flex-1"
            onkeydown={(e) => {
              if (e.key === "Enter") {
                e.preventDefault();
                addChecklistItemFromInput();
              }
            }}
          />
        </div>
      </div>

      <!-- Attachments -->
      <div class="space-y-2">
        <Label class="flex items-center gap-2">
          <ImagePlus class="h-4 w-4" />
          Images
        </Label>

        {#if (note?.attachments && note.attachments.length > 0) || pendingAttachments.length > 0}
          <div class="flex flex-wrap gap-2">
            {#if note?.attachments}
              {#each note.attachments as attachment (attachment.id)}
                <div class="relative group">
                  <div
                    class="w-16 h-16 rounded-md border bg-muted overflow-hidden"
                  >
                    <img
                      src="/api/v4/notes/{note.id}/attachments/{attachment.id}"
                      alt={attachment.fileName}
                      class="w-full h-full object-cover"
                    />
                  </div>
                  <button
                    type="button"
                    class="absolute -top-1.5 -right-1.5 bg-muted-foreground/80 text-background rounded-full p-0.5 opacity-0 group-hover:opacity-100 transition-opacity hover:bg-muted-foreground"
                    onclick={() =>
                      deleteExistingAttachment(note!.id!, attachment.id!)}
                  >
                    <X class="h-3 w-3" />
                  </button>
                </div>
              {/each}
            {/if}

            {#each pendingAttachments as file, index (index)}
              <div class="relative group">
                <div
                  class="w-16 h-16 rounded-md border bg-muted overflow-hidden"
                >
                  <img
                    src={URL.createObjectURL(file)}
                    alt={file.name}
                    class="w-full h-full object-cover"
                  />
                </div>
                <button
                  type="button"
                  class="absolute -top-1.5 -right-1.5 bg-muted-foreground/80 text-background rounded-full p-0.5 opacity-0 group-hover:opacity-100 transition-opacity hover:bg-muted-foreground"
                  onclick={() => removePendingAttachment(index)}
                >
                  <X class="h-3 w-3" />
                </button>
              </div>
            {/each}
          </div>
        {/if}

        <label
          class="flex items-center justify-center gap-2 px-4 py-3 border-2 border-dashed rounded-lg cursor-pointer hover:bg-muted/50 transition-colors text-sm text-muted-foreground"
        >
          <ImagePlus class="h-4 w-4" />
          <span>Add images</span>
          <input
            type="file"
            accept="image/*"
            multiple
            class="hidden"
            onchange={handleFileSelect}
          />
        </label>
      </div>

      <!-- Tracker Links -->
      {#if !isNewNote && note}
        <TrackerLinkSection
          trackerLinks={note.trackerLinks}
          definitions={trackerDefinitions}
          onLink={handleLinkTracker}
          onUnlink={handleUnlinkTracker}
          onUpdateThresholds={handleUpdateThresholds}
        />
      {:else if isNewNote}
        <div class="space-y-3">
          <div class="flex items-center justify-between">
            <Label class="text-sm font-medium flex items-center gap-2">
              <Link class="h-4 w-4" />
              Linked Trackers
            </Label>
            {#if availableDefinitionsForNewNote.length > 0}
              <Select.Root
                type="single"
                onValueChange={(v) => v && addFormTrackerLink(v)}
              >
                <Select.Trigger class="w-auto gap-2">
                  <Plus class="h-4 w-4" />
                  Link Tracker
                </Select.Trigger>
                <Select.Content>
                  {#each availableDefinitionsForNewNote as def}
                    {@const category = def.category ?? TrackerCategory.Custom}
                    <Select.Item value={def.id ?? ""}>
                      <span class="flex items-center gap-2">
                        <TrackerCategoryIcon {category} class="h-4 w-4" />
                        {def.name}
                      </span>
                    </Select.Item>
                  {/each}
                </Select.Content>
              </Select.Root>
            {/if}
          </div>

          {#if formTrackerLinks.length === 0}
            <div
              class="text-center py-4 text-muted-foreground text-sm border border-dashed rounded-lg"
            >
              <p>No linked trackers</p>
              <p class="text-xs mt-1">
                Link this note to a tracker to get reminded before appointments
              </p>
            </div>
          {:else}
            <div class="space-y-2">
              {#each formTrackerLinks as link (link.trackerDefinitionId)}
                {@const def = getDefinition(link.trackerDefinitionId)}
                {@const category = def?.category ?? TrackerCategory.Custom}
                {@const isExpanded = expandedNewLinks.has(
                  link.trackerDefinitionId
                )}
                {@const mode =
                  def?.mode === TrackerMode.Event ? "Event" : "Duration"}

                <div class="border rounded-lg bg-muted/30">
                  <div class="flex items-center justify-between p-3">
                    <button
                      type="button"
                      class="flex items-center gap-2 flex-1 text-left"
                      onclick={() =>
                        toggleNewLinkExpanded(link.trackerDefinitionId)}
                    >
                      <TrackerCategoryIcon
                        {category}
                        class="h-4 w-4 text-muted-foreground"
                      />
                      <span class="font-medium text-sm">
                        {def?.name ?? "Unknown Tracker"}
                      </span>
                      {#if link.thresholds.length > 0}
                        <span class="text-xs text-muted-foreground">
                          ({link.thresholds.length} reminder{link.thresholds
                            .length !== 1
                            ? "s"
                            : ""})
                        </span>
                      {/if}
                      <ChevronDown
                        class={cn(
                          "h-4 w-4 text-muted-foreground transition-transform ml-auto",
                          isExpanded && "rotate-180"
                        )}
                      />
                    </button>
                    <Button
                      variant="ghost"
                      size="icon"
                      class="h-8 w-8 text-muted-foreground hover:text-destructive shrink-0"
                      onclick={() =>
                        removeFormTrackerLink(link.trackerDefinitionId)}
                      title="Unlink tracker"
                    >
                      <Unlink class="h-4 w-4" />
                    </Button>
                  </div>

                  {#if isExpanded}
                    <div class="px-3 pb-3 pt-0">
                      <ThresholdEditor
                        thresholds={link.thresholds}
                        onthresholdsChange={(updated) =>
                          updateFormLinkThresholds(
                            link.trackerDefinitionId,
                            updated
                          )}
                        label="Reminders"
                        {mode}
                        lifespanHours={def?.lifespanHours}
                        createThreshold={createNewNoteThreshold}
                        hoursField="hoursOffset"
                        emptyDescription="Add reminders to get notified before the tracker is due"
                      />
                    </div>
                  {/if}
                </div>
              {/each}
            </div>
          {/if}
        </div>
      {/if}
    </div>

    <Dialog.Footer>
      <Button variant="outline" onclick={handleClose}>Cancel</Button>
      <Button
        onclick={saveNote}
        disabled={!formTitle.trim() || uploadingAttachment}
      >
        {#if uploadingAttachment}
          <Loader2 class="h-4 w-4 mr-2 animate-spin" />
          Uploading...
        {:else}
          {isNewNote ? "Create" : "Save"}
        {/if}
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
