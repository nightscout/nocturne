<script lang="ts">
  import { NoteCategory } from "$lib/api/generated/nocturne-api-client";
  import { cn } from "$lib/utils";
  import Eye from "lucide-svelte/icons/eye";
  import HelpCircle from "lucide-svelte/icons/help-circle";
  import CheckSquare from "lucide-svelte/icons/check-square";
  import Bookmark from "lucide-svelte/icons/bookmark";
  import type { Component } from "svelte";

  interface Props {
    category: NoteCategory;
    showLabel?: boolean;
    class?: string;
  }

  let { category, showLabel = true, class: className }: Props = $props();

  const categoryConfig: Record<
    NoteCategory,
    { icon: Component; label: string; colorClass: string }
  > = {
    [NoteCategory.Observation]: {
      icon: Eye,
      label: "Observation",
      colorClass: "bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-300",
    },
    [NoteCategory.Question]: {
      icon: HelpCircle,
      label: "Question",
      colorClass: "bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-300",
    },
    [NoteCategory.Task]: {
      icon: CheckSquare,
      label: "Task",
      colorClass: "bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300",
    },
    [NoteCategory.Marker]: {
      icon: Bookmark,
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
  <svelte:component this={config.icon} class="size-3" />
  {#if showLabel}
    <span>{config.label}</span>
  {/if}
</span>
