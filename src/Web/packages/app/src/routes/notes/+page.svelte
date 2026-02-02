<script lang="ts">
  import { Card, CardContent } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import * as ToggleGroup from "$lib/components/ui/toggle-group";
  import {
    StickyNote,
    Plus,
    Eye,
    HelpCircle,
    CheckSquare,
    Flag,
    Archive,
    ArchiveRestore,
    Trash2,
    Pencil,
    Loader2,
    AlertTriangle,
    Filter,
    Clock,
    ImagePlus,
  } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import { onMount } from "svelte";
  import { goto } from "$app/navigation";
  import { page } from "$app/stores";
  import { getAuthStore } from "$lib/stores/auth-store.svelte";
  import * as notesRemote from "$lib/data/notes.remote";
  import { NoteEditorDialog } from "$lib/components/notes";
  import {
    NoteCategory,
    type Note,
    type NoteChecklistItem,
  } from "$api";

  // Auth state
  const authStore = getAuthStore();
  const isAuthenticated = $derived(authStore.isAuthenticated);

  // State
  let loading = $state(true);
  let error = $state<string | null>(null);
  let notes = $state<Note[]>([]);

  // Filter state
  let selectedCategory = $state<NoteCategory | null>(null);
  let statusFilter = $state<"active" | "archived" | "all">("active");

  // URL-based dialog state - derive from search params
  const editNoteId = $derived($page.url.searchParams.get("edit"));
  const isNewNoteParam = $derived($page.url.searchParams.get("new") === "true");
  const urlCategory = $derived($page.url.searchParams.get("category") as NoteCategory | null);
  const urlDate = $derived($page.url.searchParams.get("date"));

  // Dialog is open if we have an edit ID or new=true
  let isNoteDialogOpen = $derived(editNoteId !== null || isNewNoteParam);
  let isNewNote = $derived(isNewNoteParam);

  // Get the note being edited from the notes array
  const editingNote = $derived.by(() => {
    if (!editNoteId) return null;
    return notes.find((n) => n.id === editNoteId) ?? null;
  });

  // Get default category from URL or use Observation
  const defaultCategory = $derived(
    urlCategory && Object.values(NoteCategory).includes(urlCategory)
      ? urlCategory
      : NoteCategory.Observation
  );

  // Get default date from URL
  const defaultDate = $derived.by(() => {
    if (!urlDate) return undefined;
    const date = new Date(urlDate);
    return isNaN(date.getTime()) ? undefined : date;
  });

  // Delete confirmation dialog state
  let isDeleteDialogOpen = $state(false);
  let deletingNoteId = $state<string | null>(null);

  // Category config
  const categoryConfig: Record<
    NoteCategory,
    {
      label: string;
      icon: typeof StickyNote;
      color: string;
      description: string;
    }
  > = {
    [NoteCategory.Observation]: {
      label: "Observation",
      icon: Eye,
      color: "text-blue-500 bg-blue-500/10",
      description: "Record patterns, symptoms, or things you notice",
    },
    [NoteCategory.Question]: {
      label: "Question",
      icon: HelpCircle,
      color: "text-purple-500 bg-purple-500/10",
      description: "Questions to ask your doctor or research later",
    },
    [NoteCategory.Task]: {
      label: "Task",
      icon: CheckSquare,
      color: "text-green-500 bg-green-500/10",
      description: "Action items with optional checklist",
    },
    [NoteCategory.Marker]: {
      label: "Marker",
      icon: Flag,
      color: "text-orange-500 bg-orange-500/10",
      description: "Mark significant events or milestones",
    },
  };

  // Filtered notes
  const filteredNotes = $derived.by(() => {
    let result = notes;

    // Filter by category
    if (selectedCategory) {
      result = result.filter((n) => n.category === selectedCategory);
    }

    // Filter by status
    if (statusFilter === "active") {
      result = result.filter((n) => !n.isArchived);
    } else if (statusFilter === "archived") {
      result = result.filter((n) => n.isArchived);
    }

    // Sort by most recent first
    return result.sort((a, b) => {
      const dateA = new Date(a.updatedAt ?? a.createdAt ?? 0);
      const dateB = new Date(b.updatedAt ?? b.createdAt ?? 0);
      return dateB.getTime() - dateA.getTime();
    });
  });

  // Counts by category
  const categoryCounts = $derived.by(() => {
    const activeNotes = notes.filter((n) =>
      statusFilter === "archived" ? n.isArchived : !n.isArchived
    );
    const counts: Record<string, number> = { all: activeNotes.length };
    for (const cat of Object.values(NoteCategory)) {
      counts[cat] = activeNotes.filter((n) => n.category === cat).length;
    }
    return counts;
  });

  // Load data
  async function loadData() {
    loading = true;
    error = null;
    try {
      const notesData = await notesRemote.getNotes(undefined);
      notes = notesData || [];
    } catch (err) {
      console.error("Failed to load notes:", err);
      error = "Failed to load notes";
    } finally {
      loading = false;
    }
  }

  onMount(() => {
    loadData();
  });

  // Format date
  function formatDate(dateStr: Date | undefined): string {
    if (!dateStr) return "";
    return new Date(dateStr).toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  }

  // Format relative time
  function formatRelativeTime(dateStr: Date | undefined): string {
    if (!dateStr) return "";
    const date = new Date(dateStr);
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffMins = Math.floor(diffMs / 60000);
    const diffHours = Math.floor(diffMs / 3600000);
    const diffDays = Math.floor(diffMs / 86400000);

    if (diffMins < 1) return "Just now";
    if (diffMins < 60) return `${diffMins}m ago`;
    if (diffHours < 24) return `${diffHours}h ago`;
    if (diffDays < 7) return `${diffDays}d ago`;
    return formatDate(dateStr);
  }

  // Redirect to login if not authenticated
  function requireAuth(): boolean {
    if (!isAuthenticated) {
      const returnUrl = encodeURIComponent(window.location.pathname);
      goto(`/auth/login?returnUrl=${returnUrl}`);
      return false;
    }
    return true;
  }

  // Helper to update URL with shallow routing
  async function updateUrl(params: {
    edit?: string | null;
    new?: string | null;
    category?: string | null;
    date?: string | null;
  }) {
    const url = new URL($page.url);
    if (params.edit !== undefined) {
      if (params.edit) {
        url.searchParams.set("edit", params.edit);
      } else {
        url.searchParams.delete("edit");
      }
    }
    if (params.new !== undefined) {
      if (params.new) {
        url.searchParams.set("new", params.new);
      } else {
        url.searchParams.delete("new");
      }
    }
    if (params.category !== undefined) {
      if (params.category) {
        url.searchParams.set("category", params.category);
      } else {
        url.searchParams.delete("category");
      }
    }
    if (params.date !== undefined) {
      if (params.date) {
        url.searchParams.set("date", params.date);
      } else {
        url.searchParams.delete("date");
      }
    }
    await goto(url.toString(), { replaceState: true, noScroll: true });
  }

  // Close dialog by clearing URL params
  function closeDialog() {
    updateUrl({ edit: null, new: null, category: null, date: null });
  }

  // Open new note dialog
  function openNewNote(category?: NoteCategory) {
    if (!requireAuth()) return;
    if (category) {
      updateUrl({ edit: null, new: "true", category: category });
    } else {
      updateUrl({ edit: null, new: "true" });
    }
  }

  // Open edit note dialog
  function openEditNote(note: Note) {
    if (!requireAuth()) return;
    updateUrl({ new: null, edit: note.id, category: null, date: null });
  }

  // Handle note saved from dialog
  async function handleNoteSaved(_savedNote: Note) {
    await loadData();
    closeDialog();
  }

  // Archive/unarchive note
  async function toggleArchive(note: Note) {
    if (!requireAuth()) return;

    try {
      await notesRemote.archiveNote({
        id: note.id!,
        archive: !note.isArchived,
      });
      await loadData();
    } catch (err) {
      console.error("Failed to archive note:", err);
    }
  }

  // Delete note
  function openDeleteDialog(id: string) {
    if (!requireAuth()) return;

    deletingNoteId = id;
    isDeleteDialogOpen = true;
  }

  async function confirmDelete() {
    if (!deletingNoteId) return;

    try {
      await notesRemote.deleteNote(deletingNoteId);
      await loadData();
      isDeleteDialogOpen = false;
      deletingNoteId = null;
    } catch (err) {
      console.error("Failed to delete note:", err);
    }
  }

  // Toggle checklist item
  async function toggleChecklistItem(note: Note, item: NoteChecklistItem) {
    if (!requireAuth()) return;
    if (!item.id) return;

    try {
      await notesRemote.toggleChecklistItem({
        noteId: note.id!,
        itemId: item.id,
      });
      await loadData();
    } catch (err) {
      console.error("Failed to toggle checklist item:", err);
    }
  }

  // Get checklist progress
  function getChecklistProgress(
    items: NoteChecklistItem[] | undefined
  ): { completed: number; total: number } | null {
    if (!items || items.length === 0) return null;
    const completed = items.filter((i) => i.isCompleted).length;
    return { completed, total: items.length };
  }
</script>

<svelte:head>
  <title>Notes & Tasks - Nocturne</title>
</svelte:head>

<div class="container mx-auto p-6 max-w-4xl">
  <!-- Header -->
  <div class="mb-8">
    <div class="flex items-center justify-between">
      <div class="flex items-center gap-3">
        <div
          class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10"
        >
          <StickyNote class="h-5 w-5 text-primary" />
        </div>
        <div>
          <h1 class="text-3xl font-bold tracking-tight">Notes & Tasks</h1>
          <p class="text-muted-foreground">
            Capture observations, questions, and to-dos
          </p>
        </div>
      </div>
      <Button onclick={() => openNewNote()}>
        <Plus class="h-4 w-4 mr-2" />
        New Note
      </Button>
    </div>
  </div>

  {#if loading}
    <div class="flex items-center justify-center py-12">
      <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
    </div>
  {:else if error}
    <Card class="border-destructive">
      <CardContent class="py-6 text-center">
        <AlertTriangle class="h-8 w-8 text-destructive mx-auto mb-2" />
        <p class="text-destructive">{error}</p>
        <Button variant="outline" class="mt-4" onclick={loadData}>Retry</Button>
      </CardContent>
    </Card>
  {:else}
    <!-- Filter Bar -->
    <div class="mb-6 space-y-4">
      <!-- Category Chips -->
      <div class="flex flex-wrap items-center gap-2">
        <Filter class="h-4 w-4 text-muted-foreground" />
        <Button
          variant={selectedCategory === null ? "default" : "outline"}
          size="sm"
          onclick={() => (selectedCategory = null)}
        >
          All
          <Badge variant="secondary" class="ml-1.5">
            {categoryCounts["all"]}
          </Badge>
        </Button>
        {#each Object.values(NoteCategory) as category}
          {@const config = categoryConfig[category]}
          {@const Icon = config.icon}
          <Button
            variant={selectedCategory === category ? "default" : "outline"}
            size="sm"
            onclick={() => (selectedCategory = category)}
            class="gap-1.5"
          >
            <Icon class="h-3.5 w-3.5" />
            {config.label}
            {#if categoryCounts[category] > 0}
              <Badge variant="secondary" class="ml-1">
                {categoryCounts[category]}
              </Badge>
            {/if}
          </Button>
        {/each}
      </div>

      <!-- Status Toggle -->
      <div class=" items-center">
        <ToggleGroup.Root
          type="single"
          spacing={0}
          value={statusFilter}
          onValueChange={(v) => {
            if (v) statusFilter = v as "active" | "archived" | "all";
          }}
          class="border rounded-md flex"
        >
          <ToggleGroup.Item value="active" class="px-3">
            Active
          </ToggleGroup.Item>
          <ToggleGroup.Item value="archived" class="px-3">
            <Archive class="h-3.5 w-3.5 shrink-0" />
            Archived
          </ToggleGroup.Item>
          <ToggleGroup.Item value="all" class="px-3">All</ToggleGroup.Item>
        </ToggleGroup.Root>
      </div>
    </div>

    <!-- Notes List -->
    {#if filteredNotes.length === 0}
      <Card>
        <CardContent class="py-12 text-center">
          <StickyNote
            class="h-12 w-12 mx-auto mb-3 text-muted-foreground opacity-50"
          />
          <p class="text-muted-foreground">
            {#if selectedCategory}
              No {categoryConfig[selectedCategory].label.toLowerCase()}s found
            {:else if statusFilter === "archived"}
              No archived notes
            {:else}
              No notes yet
            {/if}
          </p>
          <p class="text-sm text-muted-foreground mt-1">
            {#if statusFilter !== "archived"}
              Create a note to get started
            {:else}
              Archive notes to see them here
            {/if}
          </p>
          {#if statusFilter !== "archived"}
            <Button
              variant="outline"
              class="mt-4"
              onclick={() => openNewNote(selectedCategory ?? undefined)}
            >
              <Plus class="h-4 w-4 mr-2" />
              New{selectedCategory
                ? ` ${categoryConfig[selectedCategory].label}`
                : " Note"}
            </Button>
          {/if}
        </CardContent>
      </Card>
    {:else}
      <div class="space-y-3">
        {#each filteredNotes as note (note.id)}
          {@const config =
            categoryConfig[note.category ?? NoteCategory.Observation]}
          {@const Icon = config.icon}
          {@const progress = getChecklistProgress(note.checklistItems)}
          <Card
            class={cn(
              "transition-colors hover:bg-muted/50",
              note.isArchived && "opacity-60"
            )}
          >
            <CardContent class="p-4">
              <div class="flex items-start gap-3">
                <!-- Category Icon -->
                <div class={cn("p-2 rounded-lg shrink-0", config.color)}>
                  <Icon class="h-4 w-4" />
                </div>

                <!-- Content -->
                <div class="flex-1 min-w-0">
                  <div class="flex items-start justify-between gap-2">
                    <div class="flex-1 min-w-0">
                      {#if note.title}
                        <h3 class="font-medium truncate">{note.title}</h3>
                      {/if}
                      <p
                        class={cn(
                          "text-sm",
                          note.title ? "text-muted-foreground" : "font-medium",
                          "line-clamp-2"
                        )}
                      >
                        {note.content}
                      </p>
                    </div>

                    <!-- Actions -->
                    <div class="flex items-center gap-1 shrink-0">
                      <Button
                        variant="ghost"
                        size="icon"
                        class="h-8 w-8"
                        onclick={() => openEditNote(note)}
                        title="Edit"
                      >
                        <Pencil class="h-4 w-4" />
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon"
                        class="h-8 w-8"
                        onclick={() => toggleArchive(note)}
                        title={note.isArchived ? "Unarchive" : "Archive"}
                      >
                        {#if note.isArchived}
                          <ArchiveRestore class="h-4 w-4" />
                        {:else}
                          <Archive class="h-4 w-4" />
                        {/if}
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon"
                        class="h-8 w-8 text-destructive hover:text-destructive"
                        onclick={() => openDeleteDialog(note.id!)}
                        title="Delete"
                      >
                        <Trash2 class="h-4 w-4" />
                      </Button>
                    </div>
                  </div>

                  <!-- Checklist Items (inline preview) -->
                  {#if note.checklistItems && note.checklistItems.length > 0}
                    <div class="mt-3 space-y-1.5">
                      {#each note.checklistItems.slice(0, 3) as item (item.id)}
                        <label
                          class="flex items-center gap-2 text-sm cursor-pointer"
                        >
                          <Checkbox
                            checked={item.isCompleted}
                            onCheckedChange={() =>
                              toggleChecklistItem(note, item)}
                          />
                          <span
                            class={cn(
                              item.isCompleted &&
                                "line-through text-muted-foreground"
                            )}
                          >
                            {item.text}
                          </span>
                        </label>
                      {/each}
                      {#if note.checklistItems.length > 3}
                        <p class="text-xs text-muted-foreground pl-6">
                          +{note.checklistItems.length - 3} more items
                        </p>
                      {/if}
                    </div>
                  {/if}

                  <!-- Footer -->
                  <div
                    class="flex items-center gap-3 mt-3 text-xs text-muted-foreground"
                  >
                    <span class="flex items-center gap-1">
                      <Clock class="h-3 w-3" />
                      {formatRelativeTime(note.updatedAt ?? note.createdAt)}
                    </span>
                    {#if progress}
                      <span
                        class={cn(
                          "flex items-center gap-1",
                          progress.completed === progress.total &&
                            "text-green-500"
                        )}
                      >
                        <CheckSquare class="h-3 w-3" />
                        {progress.completed}/{progress.total}
                      </span>
                    {/if}
                    {#if note.attachments && note.attachments.length > 0}
                      <span class="flex items-center gap-1">
                        <ImagePlus class="h-3 w-3" />
                        {note.attachments.length}
                      </span>
                    {/if}
                    {#if note.isArchived}
                      <Badge variant="secondary" class="text-xs">
                        Archived
                      </Badge>
                    {/if}
                  </div>
                </div>
              </div>
            </CardContent>
          </Card>
        {/each}
      </div>
    {/if}
  {/if}
</div>

<!-- Note Editor Dialog -->
<NoteEditorDialog
  open={isNoteDialogOpen}
  note={isNewNote ? null : editingNote}
  defaultCategory={defaultCategory}
  defaultDate={defaultDate}
  onClose={closeDialog}
  onSave={handleNoteSaved}
/>

<!-- Delete Confirmation Dialog -->
<AlertDialog.Root bind:open={isDeleteDialogOpen}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Delete Note</AlertDialog.Title>
      <AlertDialog.Description>
        Are you sure you want to delete this note? This action cannot be undone.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel
        onclick={() => {
          isDeleteDialogOpen = false;
          deletingNoteId = null;
        }}
      >
        Cancel
      </AlertDialog.Cancel>
      <AlertDialog.Action
        onclick={confirmDelete}
        class="bg-destructive text-destructive-foreground hover:bg-destructive/90"
      >
        Delete
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
