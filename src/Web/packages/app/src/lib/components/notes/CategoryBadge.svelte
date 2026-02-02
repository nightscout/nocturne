<script lang="ts">
  import { NoteCategory } from "$lib/api/generated/nocturne-api-client";
  import { cn } from "$lib/utils";
  import Eye from "lucide-svelte/icons/eye";
  import HelpCircle from "lucide-svelte/icons/help-circle";
  import CheckSquare from "lucide-svelte/icons/check-square";
  import Bookmark from "lucide-svelte/icons/bookmark";

  interface Props {
    category: NoteCategory;
    showLabel?: boolean;
    class?: string;
  }

  let { category, showLabel = true, class: className }: Props = $props();

  const categoryConfig: Record<
    NoteCategory,
    { label: string; colorClass: string }
  > = {
    [NoteCategory.Observation]: {
      label: "Observation",
      colorClass: "bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-300",
    },
    [NoteCategory.Question]: {
      label: "Question",
      colorClass: "bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-300",
    },
    [NoteCategory.Task]: {
      label: "Task",
      colorClass: "bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300",
    },
    [NoteCategory.Marker]: {
      label: "Marker",
      colorClass: "bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-300",
    },
  };

  const config = $derived(categoryConfig[category]);
</script>

<span
  class={cn(
    "inline-flex items-center gap-1 rounded-md px-2 py-0.5 text-xs font-medium",
    config.colorClass,
    className
  )}
>
  {#if category === NoteCategory.Observation}
    <Eye class="size-3" />
  {:else if category === NoteCategory.Question}
    <HelpCircle class="size-3" />
  {:else if category === NoteCategory.Task}
    <CheckSquare class="size-3" />
  {:else if category === NoteCategory.Marker}
    <Bookmark class="size-3" />
  {/if}
  {#if showLabel}
    <span>{config.label}</span>
  {/if}
</span>
