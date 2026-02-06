<script lang="ts">
  import { NoteCategory } from "$api";
  import { cn } from "$lib/utils";
  import { categoryConfig } from "./category-config";

  interface Props {
    category: NoteCategory;
    showLabel?: boolean;
    class?: string;
  }

  let { category, showLabel = true, class: className }: Props = $props();

  const config = $derived(categoryConfig[category]);
  const Icon = $derived(config.icon);
</script>

<span
  class={cn(
    "inline-flex items-center gap-1 rounded-md px-2 py-0.5 text-xs font-medium",
    config.badgeColorClass,
    className
  )}
>
  <Icon class="size-3" />
  {#if showLabel}
    <span>{config.label}</span>
  {/if}
</span>
