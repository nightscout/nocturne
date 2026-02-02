<script lang="ts">
  import type {
    Note,
    NoteAttachment,
    TrackerDefinitionDto,
  } from "$lib/api/generated/nocturne-api-client";
  import { NoteCategory } from "$lib/api/generated/nocturne-api-client";
  import { cn } from "$lib/utils";
  import { Input } from "$lib/components/ui/input";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Button } from "$lib/components/ui/button";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";
  import * as Select from "$lib/components/ui/select";
  import * as Popover from "$lib/components/ui/popover";
  import { Calendar } from "$lib/components/ui/calendar";
  import { TrackerCategoryIcon } from "$lib/components/icons";
  import { TrackerCategory } from "$api";
  import CategoryBadge from "./CategoryBadge.svelte";
  import ChecklistEditor from "./ChecklistEditor.svelte";
  import CalendarIcon from "lucide-svelte/icons/calendar";
  import Archive from "lucide-svelte/icons/archive";
  import Trash2 from "lucide-svelte/icons/trash-2";
  import Image from "lucide-svelte/icons/image";
  import Link from "lucide-svelte/icons/link";
  import Eye from "lucide-svelte/icons/eye";
  import HelpCircle from "lucide-svelte/icons/help-circle";
  import CheckSquare from "lucide-svelte/icons/check-square";
  import Bookmark from "lucide-svelte/icons/bookmark";
  import {
    CalendarDate,
    DateFormatter,
    getLocalTimeZone,
    type DateValue,
  } from "@internationalized/date";

  interface Props {
    note: Note;
    attachments?: NoteAttachment[];
    /** Tracker definitions to resolve tracker names */
    trackerDefinitions?: TrackerDefinitionDto[];
    onSave?: (note: Note) => void;
    onArchive?: () => void;
    onDelete?: () => void;
    readonly?: boolean;
    class?: string;
  }

  let {
    note = $bindable(),
    attachments = [],
    trackerDefinitions = [],
    onSave,
    onArchive,
    onDelete,
    readonly = false,
    class: className,
  }: Props = $props();

  // Get tracker definition by ID
  function getTrackerDefinition(id: string | undefined): TrackerDefinitionDto | undefined {
    if (!id) return undefined;
    return trackerDefinitions.find((d) => d.id === id);
  }

  // Date formatter for display
  const df = new DateFormatter("en-US", {
    dateStyle: "long",
  });

  // Convert Date to CalendarDate for the calendar component
  const occurredDateValue = $derived.by(() => {
    if (!note.occurredAt) return undefined;
    const date = new Date(note.occurredAt);
    return new CalendarDate(
      date.getFullYear(),
      date.getMonth() + 1,
      date.getDate()
    );
  });

  // Handle date selection
  function handleDateSelect(value: DateValue | undefined) {
    if (value) {
      note.occurredAt = value.toDate(getLocalTimeZone());
    } else {
      note.occurredAt = undefined;
    }
    onSave?.(note);
  }

  // Category options with icons
  const categoryOptions = [
    { value: NoteCategory.Observation, label: "Observation", icon: Eye },
    { value: NoteCategory.Question, label: "Question", icon: HelpCircle },
    { value: NoteCategory.Task, label: "Task", icon: CheckSquare },
    { value: NoteCategory.Marker, label: "Marker", icon: Bookmark },
  ];

  // Handle category change
  function handleCategoryChange(value: string | undefined) {
    if (value) {
      note.category = value as NoteCategory;
      onSave?.(note);
    }
  }

  // Handle title/content changes with debounce
  let saveTimeout: ReturnType<typeof setTimeout> | undefined;

  function handleTextChange() {
    if (saveTimeout) clearTimeout(saveTimeout);
    saveTimeout = setTimeout(() => {
      onSave?.(note);
    }, 500);
  }
</script>

<div class={cn("flex flex-col gap-6", className)}>
  <!-- Header with category selector -->
  <div class="flex items-center justify-between gap-4">
    <div class="flex-1">
      {#if readonly}
        <CategoryBadge category={note.category!} />
      {:else}
        <Select.Root
          type="single"
          value={note.category}
          onValueChange={handleCategoryChange}
        >
          <Select.Trigger class="w-[180px]">
            {#if note.category}
              <CategoryBadge category={note.category} />
            {:else}
              <span class="text-muted-foreground">Select category</span>
            {/if}
          </Select.Trigger>
          <Select.Content>
            {#each categoryOptions as option (option.value)}
              <Select.Item value={option.value}>
                <div class="flex items-center gap-2">
                  {#if option.value === NoteCategory.Observation}
                    <Eye class="size-4" />
                  {:else if option.value === NoteCategory.Question}
                    <HelpCircle class="size-4" />
                  {:else if option.value === NoteCategory.Task}
                    <CheckSquare class="size-4" />
                  {:else if option.value === NoteCategory.Marker}
                    <Bookmark class="size-4" />
                  {/if}
                  <span>{option.label}</span>
                </div>
              </Select.Item>
            {/each}
          </Select.Content>
        </Select.Root>
      {/if}
    </div>

    <!-- Actions -->
    {#if !readonly}
      <div class="flex items-center gap-2">
        {#if onArchive}
          <Button variant="outline" size="sm" onclick={onArchive}>
            <Archive class="size-4" />
            <span class="hidden sm:inline">
              {note.isArchived ? "Unarchive" : "Archive"}
            </span>
          </Button>
        {/if}
        {#if onDelete}
          <Button variant="destructive" size="sm" onclick={onDelete}>
            <Trash2 class="size-4" />
            <span class="hidden sm:inline">Delete</span>
          </Button>
        {/if}
      </div>
    {/if}
  </div>

  <!-- Title -->
  <div class="space-y-2">
    <Label for="note-title">Title</Label>
    {#if readonly}
      <p class="text-lg font-medium">
        {note.title || "(No title)"}
      </p>
    {:else}
      <Input
        id="note-title"
        type="text"
        placeholder="Add a title..."
        bind:value={note.title}
        oninput={handleTextChange}
      />
    {/if}
  </div>

  <!-- Content -->
  <div class="space-y-2">
    <Label for="note-content">Content</Label>
    {#if readonly}
      <p class="whitespace-pre-wrap text-sm text-foreground">
        {note.content || "(No content)"}
      </p>
    {:else}
      <Textarea
        id="note-content"
        placeholder="Write your note..."
        bind:value={note.content}
        oninput={handleTextChange}
        class="min-h-[120px]"
      />
    {/if}
  </div>

  <!-- Occurred date -->
  <div class="space-y-2">
    <Label>Occurred Date</Label>
    {#if readonly}
      <p class="text-sm">
        {note.occurredAt
          ? df.format(new Date(note.occurredAt))
          : "(No date specified)"}
      </p>
    {:else}
      <Popover.Root>
        <Popover.Trigger>
          <Button
            variant="outline"
            class="w-full justify-start text-left font-normal"
          >
            <CalendarIcon class="mr-2 size-4" />
            {#if occurredDateValue}
              {df.format(occurredDateValue.toDate(getLocalTimeZone()))}
            {:else}
              <span class="text-muted-foreground">Pick a date</span>
            {/if}
          </Button>
        </Popover.Trigger>
        <Popover.Content class="w-auto p-0">
          <Calendar
            type="single"
            value={occurredDateValue}
            onValueChange={handleDateSelect}
          />
        </Popover.Content>
      </Popover.Root>
    {/if}
  </div>

  <!-- Checklist section (only for Task category) -->
  {#if note.category === NoteCategory.Task}
    <Separator />
    <div class="space-y-2">
      <Label>Checklist</Label>
      <ChecklistEditor
        bind:items={note.checklistItems}
        {readonly}
        onToggle={() => onSave?.(note)}
      />
    </div>
  {/if}

  <!-- Attachments section -->
  {#if attachments.length > 0 || !readonly}
    <Separator />
    <div class="space-y-2">
      <Label class="flex items-center gap-2">
        <Image class="size-4" />
        Attachments
      </Label>
      {#if attachments.length > 0}
        <div class="grid grid-cols-3 gap-2 sm:grid-cols-4">
          {#each attachments as attachment (attachment.id)}
            <div
              class="aspect-square overflow-hidden rounded-md border bg-muted"
            >
              {#if attachment.mimeType?.startsWith("image/")}
                <!-- Image preview would go here, using a placeholder for now -->
                <div
                  class="flex h-full w-full items-center justify-center text-muted-foreground"
                >
                  <Image class="size-8" />
                </div>
              {:else}
                <div
                  class="flex h-full w-full flex-col items-center justify-center gap-1 p-2 text-center text-muted-foreground"
                >
                  <span class="text-xs line-clamp-2">
                    {attachment.fileName}
                  </span>
                </div>
              {/if}
            </div>
          {/each}
        </div>
      {:else if !readonly}
        <p class="text-sm text-muted-foreground">No attachments</p>
      {/if}
    </div>
  {/if}

  <!-- Tracker links section -->
  {#if (note.trackerLinks && note.trackerLinks.length > 0) || !readonly}
    <Separator />
    <div class="space-y-2">
      <Label class="flex items-center gap-2">
        <Link class="size-4" />
        Linked Trackers
      </Label>
      {#if note.trackerLinks && note.trackerLinks.length > 0}
        <div class="flex flex-wrap gap-2">
          {#each note.trackerLinks as link (link.id)}
            {@const def = getTrackerDefinition(link.trackerDefinitionId)}
            {@const category = def?.category ?? TrackerCategory.Custom}
            <div
              class="inline-flex items-center gap-1.5 rounded-md bg-secondary px-2 py-1 text-sm"
            >
              <TrackerCategoryIcon {category} class="size-3.5" />
              <span>{def?.name ?? "Unknown Tracker"}</span>
              {#if link.thresholds && link.thresholds.length > 0}
                <span class="text-xs text-muted-foreground">
                  ({link.thresholds.length} reminder{link.thresholds.length !== 1 ? "s" : ""})
                </span>
              {/if}
            </div>
          {/each}
        </div>
      {:else if !readonly}
        <p class="text-sm text-muted-foreground">No linked trackers</p>
      {/if}
    </div>
  {/if}

  <!-- Metadata -->
  <Separator />
  <div class="space-y-1 text-xs text-muted-foreground">
    {#if note.createdAt}
      <p>Created: {df.format(new Date(note.createdAt))}</p>
    {/if}
    {#if note.updatedAt}
      <p>Updated: {df.format(new Date(note.updatedAt))}</p>
    {/if}
    {#if note.isArchived}
      <p class="text-amber-600">This note is archived</p>
    {/if}
  </div>
</div>
