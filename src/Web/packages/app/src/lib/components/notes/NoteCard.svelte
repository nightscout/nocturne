<script lang="ts">
  import type { Note } from "$lib/api/generated/nocturne-api-client";
  import { NoteCategory } from "$lib/api/generated/nocturne-api-client";
  import { cn, timeAgo } from "$lib/utils";
  import { Badge } from "$lib/components/ui/badge";
  import CategoryBadge from "./CategoryBadge.svelte";
  import Paperclip from "lucide-svelte/icons/paperclip";
  import Link from "lucide-svelte/icons/link";

  interface Props {
    note: Note;
    onclick?: () => void;
    class?: string;
  }

  let { note, onclick, class: className }: Props = $props();

  // Truncate content for preview
  const truncatedContent = $derived(() => {
    const content = note.content || "";
    const maxLength = 120;
    if (content.length <= maxLength) return content;
    return content.substring(0, maxLength).trim() + "...";
  });

  // Calculate checklist progress for Task category
  const checklistProgress = $derived(() => {
    if (note.category !== NoteCategory.Task || !note.checklistItems?.length) {
      return null;
    }
    const completed = note.checklistItems.filter((item) => item.isCompleted).length;
    const total = note.checklistItems.length;
    return { completed, total };
  });

  // Format the occurred date
  const formattedDate = $derived(() => {
    if (!note.occurredAt) return null;

    const occurredDate = new Date(note.occurredAt);
    const now = new Date();
    const diffDays = Math.floor(
      (now.getTime() - occurredDate.getTime()) / (1000 * 60 * 60 * 24)
    );

    // Use relative time for recent dates (< 7 days)
    if (diffDays < 7) {
      return timeAgo(occurredDate.getTime());
    }

    // Use absolute date for older entries
    return occurredDate.toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
      year: occurredDate.getFullYear() !== now.getFullYear() ? "numeric" : undefined,
    });
  });

  // Check for attachments (placeholder - will depend on API)
  const attachmentCount = $derived(0); // Note model doesn't have attachments array yet

  // Check for tracker links
  const trackerLinkCount = $derived(note.trackerLinks?.length || 0);
</script>

<button
  type="button"
  class={cn(
    "group w-full rounded-lg border bg-card p-3 text-left shadow-sm transition-all",
    "hover:border-primary/50 hover:shadow-md",
    "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
    note.isArchived && "opacity-60",
    className
  )}
  onclick={onclick}
>
  <div class="flex items-start gap-3">
    <!-- Category icon -->
    <div class="shrink-0 pt-0.5">
      <CategoryBadge category={note.category!} showLabel={false} />
    </div>

    <!-- Content -->
    <div class="min-w-0 flex-1">
      <!-- Title or truncated content -->
      <div class="mb-1">
        {#if note.title}
          <h3 class="font-medium text-sm text-foreground line-clamp-1">
            {note.title}
          </h3>
          {#if note.content}
            <p class="text-xs text-muted-foreground line-clamp-2 mt-0.5">
              {truncatedContent()}
            </p>
          {/if}
        {:else}
          <p class="text-sm text-foreground line-clamp-2">
            {truncatedContent()}
          </p>
        {/if}
      </div>

      <!-- Meta information row -->
      <div class="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
        <!-- Date -->
        {#if formattedDate()}
          <span>{formattedDate()}</span>
        {/if}

        <!-- Checklist progress for Tasks -->
        {#if checklistProgress()}
          <Badge variant="secondary" class="h-5 px-1.5 text-xs">
            {checklistProgress()?.completed}/{checklistProgress()?.total}
          </Badge>
        {/if}

        <!-- Attachment count -->
        {#if attachmentCount > 0}
          <span class="inline-flex items-center gap-0.5">
            <Paperclip class="size-3" />
            {attachmentCount}
          </span>
        {/if}

        <!-- Tracker links -->
        {#if trackerLinkCount > 0}
          <span class="inline-flex items-center gap-0.5">
            <Link class="size-3" />
            {trackerLinkCount}
          </span>
        {/if}
      </div>
    </div>
  </div>
</button>
